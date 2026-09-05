// minacalc-standalone
//
// A small, dependency-free command-line wrapper around Etterna's MinaCalc
// difficulty calculator (src/Etterna/MinaCalc from the etterna repo, built
// here with -DSTANDALONE_CALC so it needs none of the game engine).
//
// Contract (matches MinterludeCalc.Core/MinaCalc.cs):
//   - Invoked as:  msd[.exe] --goal <0-1 double> --keys <int>
//   - Reads a JSON array of notes from stdin:
//         [ { "notes": <bitmask int>, "time": <seconds double> }, ... ]
//     "notes" is a column bitmask (bit i set = a tap/hold-head exists in
//     column i on this row), "time" is the row's absolute time in SECONDS.
//   - Writes a single JSON object of skillset name -> MSD value to stdout:
//         { "Overall": .., "Stream": .., "Jumpstream": .., "Handstream": ..,
//           "Stamina": .., "JackSpeed": .., "Chordjack": .., "Technical": .. }
//
// Extra (optional) flags, not required by the .NET side but useful for
// scripting / testing:
//   --rate <float>       music rate multiplier fed to MinaCalc (default 1.0)
//   --file <path>        read the notes JSON from a file instead of stdin
//   --version             print MinaCalc's internal version number and exit

#include "Etterna/MinaCalc/MinaCalc.h"

#include <algorithm>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <fstream>
#include <iostream>
#include <sstream>
#include <string>
#include <vector>

namespace {

// ---------------------------------------------------------------------
// Minimal JSON reading, just enough for `[ { "notes": N, "time": T }, ... ]`.
// No external dependency is pulled in on purpose: it keeps the build a
// single self-contained binary on every platform with nothing extra to
// fetch, vendor, or link.
// ---------------------------------------------------------------------

struct JsonParser
{
	const char* p;
	const char* end;

	explicit JsonParser(const std::string& s)
	  : p(s.data())
	  , end(s.data() + s.size())
	{
	}

	void SkipWs()
	{
		while (p < end &&
			   (*p == ' ' || *p == '\t' || *p == '\n' || *p == '\r')) {
			++p;
		}
	}

	[[noreturn]] void Fail(const std::string& msg)
	{
		throw std::runtime_error("JSON parse error: " + msg);
	}

	char Peek()
	{
		SkipWs();
		if (p >= end)
			Fail("unexpected end of input");
		return *p;
	}

	void Expect(char c)
	{
		SkipWs();
		if (p >= end || *p != c) {
			Fail(std::string("expected '") + c + "'");
		}
		++p;
	}

	bool TryConsume(char c)
	{
		SkipWs();
		if (p < end && *p == c) {
			++p;
			return true;
		}
		return false;
	}

	// Parses a JSON string, unescaping it. Field names in our schema never
	// need unicode escapes, so \\uXXXX is deliberately unsupported.
	std::string ParseString()
	{
		Expect('"');
		std::string out;
		while (p < end && *p != '"') {
			char c = *p++;
			if (c == '\\' && p < end) {
				char esc = *p++;
				switch (esc) {
					case '"':
						out += '"';
						break;
					case '\\':
						out += '\\';
						break;
					case '/':
						out += '/';
						break;
					case 'n':
						out += '\n';
						break;
					case 't':
						out += '\t';
						break;
					case 'r':
						out += '\r';
						break;
					case 'b':
						out += '\b';
						break;
					case 'f':
						out += '\f';
						break;
					default:
						out += esc;
						break;
				}
			} else {
				out += c;
			}
		}
		Expect('"');
		return out;
	}

	double ParseNumber()
	{
		SkipWs();
		const char* start = p;
		if (p < end && (*p == '-' || *p == '+'))
			++p;
		while (p < end &&
			   ((*p >= '0' && *p <= '9') || *p == '.' || *p == 'e' ||
				*p == 'E' || *p == '+' || *p == '-')) {
			++p;
		}
		if (p == start)
			Fail("expected a number");
		return std::strtod(std::string(start, p).c_str(), nullptr);
	}

	void SkipValue()
	{
		SkipWs();
		if (p >= end)
			Fail("unexpected end of input");
		switch (*p) {
			case '"':
				ParseString();
				break;
			case '{': {
				Expect('{');
				if (!TryConsume('}')) {
					do {
						SkipWs();
						ParseString();
						Expect(':');
						SkipValue();
					} while (TryConsume(','));
					Expect('}');
				}
				break;
			}
			case '[': {
				Expect('[');
				if (!TryConsume(']')) {
					do {
						SkipValue();
					} while (TryConsume(','));
					Expect(']');
				}
				break;
			}
			case 't':
				p += 4;
				break; // true
			case 'f':
				p += 5;
				break; // false
			case 'n':
				p += 4;
				break; // null
			default:
				ParseNumber();
				break;
		}
	}
};

// Reads `[ { "notes": <int>, "time": <double> }, ... ]` (key order and
// casing don't matter, unknown keys are skipped) into MinaCalc's NoteInfo.
std::vector<NoteInfo>
ParseNotes(const std::string& json)
{
	JsonParser parser(json);
	std::vector<NoteInfo> notes;

	parser.SkipWs();
	if (parser.p >= parser.end)
		return notes;

	parser.Expect('[');
	if (parser.TryConsume(']'))
		return notes;

	do {
		NoteInfo ni{};
		ni.notes = 0U;
		ni.rowTime = 0.F;

		parser.Expect('{');
		if (!parser.TryConsume('}')) {
			do {
				std::string key = parser.ParseString();
				parser.Expect(':');

				// Accept both the wire schema ("notes"/"time") and a couple
				// of forgiving aliases in case a different producer script
				// feeds this tool directly.
				if (key == "notes" || key == "Notes" || key == "mask" ||
					key == "row_notes") {
					ni.notes = static_cast<unsigned int>(parser.ParseNumber());
				} else if (key == "time" || key == "Time" ||
						   key == "rowTime" || key == "row_time") {
					ni.rowTime = static_cast<float>(parser.ParseNumber());
				} else {
					parser.SkipValue();
				}
			} while (parser.TryConsume(','));
			parser.Expect('}');
		}

		notes.push_back(ni);
	} while (parser.TryConsume(','));

	parser.Expect(']');
	return notes;
}

std::string
ReadAll(std::istream& in)
{
	std::ostringstream ss;
	ss << in.rdbuf();
	return ss.str();
}

const char* kSkillsetNames[NUM_Skillset] = {
	"Overall",	  "Stream",	   "Jumpstream", "Handstream",
	"Stamina", "JackSpeed", "Chordjack",  "Technical",
};

void
PrintUsage(const char* argv0)
{
	std::cerr
	  << "usage: " << argv0
	  << " --goal <0-1> --keys <n> [--rate <float>] [--file <path>]\n"
		 "       "
	  << argv0 << " --version\n\n"
	  << "Reads a JSON array of notes from stdin (or --file):\n"
		 "  [ { \"notes\": <column bitmask>, \"time\": <seconds> }, ... ]\n"
		 "Writes a JSON object of skillset -> MSD value to stdout.\n";
}

} // namespace

int
main(int argc, char** argv)
{
	double goal = 0.93;
	unsigned keys = 4;
	float rate = 1.0F;
	std::string filePath;
	bool wantVersion = false;

	for (int i = 1; i < argc; ++i) {
		std::string arg = argv[i];
		auto nextArg = [&](const char* flag) -> std::string {
			if (i + 1 >= argc) {
				std::cerr << "missing value for " << flag << "\n";
				std::exit(2);
			}
			return argv[++i];
		};

		if (arg == "--goal") {
			goal = std::strtod(nextArg("--goal").c_str(), nullptr);
		} else if (arg == "--keys") {
			keys = static_cast<unsigned>(
			  std::strtoul(nextArg("--keys").c_str(), nullptr, 10));
		} else if (arg == "--rate") {
			rate = std::strtof(nextArg("--rate").c_str(), nullptr);
		} else if (arg == "--file") {
			filePath = nextArg("--file");
		} else if (arg == "--version") {
			wantVersion = true;
		} else if (arg == "-h" || arg == "--help") {
			PrintUsage(argv[0]);
			return 0;
		} else {
			std::cerr << "unrecognized argument: " << arg << "\n";
			PrintUsage(argv[0]);
			return 2;
		}
	}

	if (wantVersion) {
		std::cout << "{\"calcVersion\":" << GetCalcVersion() << "}"
				   << std::endl;
		return 0;
	}

	if (keys < 1 || keys > 10) {
		std::cerr << "--keys must be between 1 and 10\n";
		return 2;
	}
	if (goal <= 0.0 || goal > 1.0) {
		std::cerr << "--goal must be in (0, 1]\n";
		return 2;
	}

	std::string input;
	try {
		if (!filePath.empty()) {
			std::ifstream f(filePath, std::ios::binary);
			if (!f) {
				std::cerr << "could not open --file '" << filePath << "'\n";
				return 2;
			}
			input = ReadAll(f);
		} else {
			input = ReadAll(std::cin);
		}
	} catch (const std::exception& ex) {
		std::cerr << "failed to read input: " << ex.what() << "\n";
		return 2;
	}

	std::vector<NoteInfo> notes;
	try {
		notes = ParseNotes(input);
	} catch (const std::exception& ex) {
		std::cerr << "failed to parse notes JSON: " << ex.what() << "\n";
		return 2;
	}

	if (notes.empty()) {
		std::cerr << "no notes supplied\n";
		return 2;
	}

	// Notes must be sorted by time for the calc to behave; guard against a
	// producer that hands them over out of order.
	std::stable_sort(
	  notes.begin(), notes.end(), [](const NoteInfo& a, const NoteInfo& b) {
		  return a.rowTime < b.rowTime;
	  });

	std::vector<float> result;
	try {
		Calc calc;
		result =
		  MinaSDCalc(notes, rate, static_cast<float>(goal), keys, &calc);
	} catch (const std::exception& ex) {
		std::cerr << "MinaCalc threw: " << ex.what() << "\n";
		return 1;
	} catch (...) {
		std::cerr << "MinaCalc threw an unknown exception\n";
		return 1;
	}

	std::ostringstream out;
	out << "{";
	for (size_t i = 0; i < result.size() && i < NUM_Skillset; ++i) {
		if (i > 0)
			out << ",";
		out << "\"" << kSkillsetNames[i] << "\":" << result[i];
	}
	out << "}";

	std::cout << out.str() << std::endl;
	return 0;
}
