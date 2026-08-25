using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fastcull.Services
{
    /// <summary>The shape persisted to disk. Kept separate so the file format is explicit.</summary>
    public sealed class AppSettingsData
    {
        [JsonPropertyName("lastFolder")]
        public string? LastFolder { get; set; }

        /// <summary>
        /// PRD 1.8.2's reverse geocoding. **Off unless the user turns it on**, which is a
        /// deliberate default rather than caution: the feature fills one optional metadata field,
        /// the app degrades to raw coordinates without it, and every lookup is a request against a
        /// donated community service. Shipping it on would put load there on behalf of users who
        /// mostly will not notice the field either way.
        /// </summary>
        [JsonPropertyName("geocodingEnabled")]
        public bool GeocodingEnabled { get; set; }

        /// <summary>
        /// The reverse-geocoding endpoint. **Configurable because Nominatim's usage policy
        /// requires it**: an application must be able to switch away from the public instance at
        /// the operator's request *"without requiring a software update"*. A compiled-in constant
        /// cannot do that - every installed copy would keep calling the old endpoint until someone
        /// shipped a new build, which is exactly the situation the clause exists to prevent.
        ///
        /// Null means "use the default". Any Nominatim-compatible endpoint can be substituted,
        /// including a self-hosted instance or a commercial provider.
        /// </summary>
        [JsonPropertyName("geocodingEndpoint")]
        public string? GeocodingEndpoint { get; set; }
    }

    /// <summary>
    /// App-level settings, currently just the last folder opened (PRD 1.1.1).
    ///
    /// **Not `ApplicationData.Current.LocalSettings`**, which is the obvious WinRT answer and does
    /// not work here: this app runs unpackaged, and `ApplicationData.Current` throws
    /// `InvalidOperationException` in that configuration. That is measured rather than assumed - it
    /// is already in this project's crash log. A plain JSON file has no packaging requirement, and
    /// can be inspected or deleted by hand, which matters for a setting whose failure mode is
    /// "opens the wrong folder".
    ///
    /// **Not the session database either.** Each session DB is scoped to one folder, so none of
    /// them can answer "which folder was open last" - that is a fact about the application, not
    /// about any one card.
    ///
    /// Every operation is best-effort and total: a missing, unreadable or malformed file reads as
    /// "no last folder", and a failed write costs the next launch its auto-resume and nothing more.
    /// Neither may take the app down or stop a folder from opening.
    /// </summary>
    public static class AppSettings
    {
        private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

        /// <summary>Beside the sessions directory, so all of the app's state lives in one place.</summary>
        public static string SettingsPath => Path.Combine(RootDirectory, "settings.json");

        public static string RootDirectory => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FastCull");

        /// <summary>
        /// The last folder opened, or null when there is none to resume.
        ///
        /// **Returns null for a folder that no longer exists**, so a deleted directory or an
        /// unplugged card is indistinguishable from a first run at the call site - PRD 1.1.1 wants
        /// both to land on the same empty state. <see cref="ReadRaw"/> exposes the recorded path
        /// regardless, so the empty state can say which folder it could not open.
        /// </summary>
        public static string? GetResumableFolder()
        {
            var recorded = ReadRaw();
            if (string.IsNullOrWhiteSpace(recorded)) return null;

            try
            {
                return Directory.Exists(recorded) ? recorded : null;
            }
            catch (Exception)
            {
                // An inaccessible path - a disconnected network share throws rather than
                // returning false - is as good as absent.
                return null;
            }
        }

        /// <summary>The recorded path whether or not it still resolves. For the empty state's message.</summary>
        public static string? ReadRaw()
        {
            try
            {
                if (!File.Exists(SettingsPath)) return null;

                var json = File.ReadAllText(SettingsPath);
                var data = JsonSerializer.Deserialize<AppSettingsData>(json);

                var folder = data?.LastFolder;
                return string.IsNullOrWhiteSpace(folder) ? null : folder;
            }
            catch (Exception)
            {
                // Malformed JSON, a partially written file, a locked file - all read as "nothing
                // remembered" rather than as an error worth surfacing.
                return null;
            }
        }

        /// <summary>
        /// The whole settings file, or defaults when there is none. Never throws.
        /// </summary>
        public static AppSettingsData Read()
        {
            try
            {
                if (!File.Exists(SettingsPath)) return new AppSettingsData();

                return JsonSerializer.Deserialize<AppSettingsData>(File.ReadAllText(SettingsPath))
                       ?? new AppSettingsData();
            }
            catch (Exception)
            {
                // Malformed JSON, a partially written file, a locked file - all read as defaults
                // rather than as an error worth surfacing.
                return new AppSettingsData();
            }
        }

        /// <summary>Writes the whole file. Returns false if it could not be written.</summary>
        public static bool Write(AppSettingsData data)
        {
            try
            {
                Directory.CreateDirectory(RootDirectory);
                File.WriteAllText(SettingsPath, JsonSerializer.Serialize(data, SerializerOptions));
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Records the folder to reopen next launch. Returns false if it could not be written,
        /// which callers may log but must not treat as a failure of the folder open itself.
        ///
        /// **Read-modify-write, not a fresh object.** This used to serialise
        /// <c>new AppSettingsData { LastFolder = … }</c>, which was harmless while the file held
        /// one field and would silently erase every other setting the moment it held two.
        /// </summary>
        public static bool SetLastFolder(string? folderPath)
        {
            var data = Read();
            data.LastFolder = folderPath;
            return Write(data);
        }

        /// <summary>PRD 1.8.2. False unless the user has turned geocoding on.</summary>
        public static bool GeocodingEnabled => Read().GeocodingEnabled;

        public static bool SetGeocodingEnabled(bool enabled)
        {
            var data = Read();
            data.GeocodingEnabled = enabled;
            return Write(data);
        }

        /// <summary>
        /// The configured endpoint, or the default when none is set. Callers get a usable value
        /// either way, so nothing has to special-case a fresh install.
        /// </summary>
        public static string GeocodingEndpoint
        {
            get
            {
                var configured = Read().GeocodingEndpoint;
                return string.IsNullOrWhiteSpace(configured) ? DefaultGeocodingEndpoint : configured!;
            }
        }

        /// <summary>Where lookups go unless settings.json says otherwise.</summary>
        public const string DefaultGeocodingEndpoint = "https://nominatim.openstreetmap.org/reverse";
    }
}
