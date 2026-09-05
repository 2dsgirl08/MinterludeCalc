using MinterludeCalc;

// Vector generation/verification runs without Interlude, so it gets first look
// at the arguments; anything else falls through to the live monitor.
if (VectorCommands.TryRun(args))
    return;

var app = new Application();
app.Run();
