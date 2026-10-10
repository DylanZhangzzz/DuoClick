using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace DuoClick
{
    internal static class CaptureLimitSettings
    {
        internal const int MinimumCapacity = 1;
        internal const int MaximumCapacity = 100;
        internal const int DefaultCapacity = 3;

        internal static string DefaultPath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "DuoClick", "capture-limit.txt");
            }
        }

        // Deriving this path from the injected language-settings path gives
        // integration tests an isolated settings directory without changing
        // production's LocalAppData location.
        internal static string GetPathForLanguageSettingsPath(string languageSettingsPath)
        {
            if (String.IsNullOrEmpty(languageSettingsPath))
                return DefaultPath;

            string fullLanguagePath = Path.GetFullPath(languageSettingsPath);
            string directory = Path.GetDirectoryName(fullLanguagePath);
            return Path.Combine(directory, "capture-limit.txt");
        }

        internal static CaptureLimitLoadResult Load(string path)
        {
            if (String.IsNullOrEmpty(path) || !File.Exists(path))
                return new CaptureLimitLoadResult(DefaultCapacity, null);

            try
            {
                string value = File.ReadAllText(path, Encoding.UTF8).Trim();
                int capacity;
                if (!Int32.TryParse(value, NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out capacity) ||
                    capacity < MinimumCapacity || capacity > MaximumCapacity)
                {
                    return new CaptureLimitLoadResult(DefaultCapacity,
                        new InvalidDataException("The saved capture limit is not supported."));
                }

                return new CaptureLimitLoadResult(capacity, null);
            }
            catch (Exception error)
            {
                return new CaptureLimitLoadResult(DefaultCapacity, error);
            }
        }

        internal static bool TrySave(string path, int capacity, out Exception error)
        {
            try
            {
                ValidateCapacity(capacity);
                if (String.IsNullOrEmpty(path))
                    throw new ArgumentException("A capture limit settings path is required.",
                        "path");

                string directory = Path.GetDirectoryName(path);
                if (!String.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);
                File.WriteAllText(path, capacity.ToString(CultureInfo.InvariantCulture),
                    new UTF8Encoding(false));
                error = null;
                return true;
            }
            catch (Exception exception)
            {
                error = exception;
                return false;
            }
        }

        internal static void ValidateCapacity(int capacity)
        {
            if (capacity < MinimumCapacity || capacity > MaximumCapacity)
                throw new ArgumentOutOfRangeException("capacity", capacity,
                    "The capture limit must be between 1 and 100.");
        }
    }

    internal sealed class CaptureLimitLoadResult
    {
        internal CaptureLimitLoadResult(int capacity, Exception error)
        {
            Capacity = capacity;
            Error = error;
        }

        internal int Capacity { get; private set; }
        internal Exception Error { get; private set; }
    }
}
