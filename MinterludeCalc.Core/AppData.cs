namespace MinterludeCalc
{
    /// <summary>
    /// Where MinterludeCalc keeps its own files (profiles, the score cache).
    /// Deliberately not next to the executable - that gets wiped by a rebuild -
    /// and never inside Interlude's own directories, which we only ever read.
    /// </summary>
    public static class AppData
    {
        public static string Directory
        {
            get
            {
                string root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

                // ApplicationData can come back empty on a stripped-down Linux
                // container; fall back to the user's home rather than writing
                // into whatever the working directory happens to be.
                if (string.IsNullOrEmpty(root))
                    root = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

                return Path.Combine(root, "MinterludeCalc");
            }
        }

        public static string PathTo(string fileName) => Path.Combine(Directory, fileName);

        public static void EnsureDirectory() => System.IO.Directory.CreateDirectory(Directory);
    }
}
