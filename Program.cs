using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

class AppConfig
{
    public string AudDApiToken { get; set; } 
    public string LinksFileName { get; set; }
    public string DownloadFolderName { get; set; }
    public string LogFileName { get; set; }
    public string SongNamesFileName { get; set; }
    public string FailedLinksFileName { get; set; }
    public int DurationThresholdSeconds { get; set; }
    public string YtDlpExtraFlags { get; set; }
}

class Program
{
    private static AppConfig Config = new AppConfig();
    private static async Task EnsureDependenciesAsync(HttpClient client)
    {
        if (!IsCommandAvailable("yt-dlp"))
        {
            Log("[INFO]: 'yt-dlp' was not found. Downloading yt-dlp.exe...");
            try
            {
                string ytDlpUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";
                byte[] bytes = await client.GetByteArrayAsync(ytDlpUrl);
                await File.WriteAllBytesAsync("yt-dlp.exe", bytes);
                Log("[SUCCESS]: Successfully downloaded yt-dlp.exe.");
            }
            catch (Exception ex)
            {
                Log($"[ERROR]: Failed to download yt-dlp.exe automatically: {ex.Message}");
            }
        }

        if (!IsCommandAvailable("ffmpeg"))
        {
            Log("[INFO]: 'ffmpeg' was not found. Downloading FFmpeg package...");
            try
            {
                string ffmpegZipUrl = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip";
                string tempZip = Path.Combine(Path.GetTempPath(), "ffmpeg_download.zip");

                byte[] zipBytes = await client.GetByteArrayAsync(ffmpegZipUrl);
                await File.WriteAllBytesAsync(tempZip, zipBytes);

                Log("[INFO]: Extracting ffmpeg.exe and ffprobe.exe...");
                using (var archive = ZipFile.OpenRead(tempZip))
                {
                    foreach (var entry in archive.Entries)
                    {
                        if (entry.Name.Equals("ffmpeg.exe", StringComparison.OrdinalIgnoreCase) ||
                            entry.Name.Equals("ffprobe.exe", StringComparison.OrdinalIgnoreCase))
                        {
                            entry.ExtractToFile(entry.Name, overwrite: true);
                        }
                    }
                }

                if (File.Exists(tempZip)) File.Delete(tempZip);
                Log("[SUCCESS]: Successfully extracted ffmpeg.exe and ffprobe.exe.");
            }
            catch (Exception ex)
            {
                Log($"[ERROR]: Failed to download FFmpeg automatically: {ex.Message}");
            }
        }
    }

    private static bool IsCommandAvailable(string command)
    {
        // First check if file exists in local application folder
        if (File.Exists($"{command}.exe") || File.Exists(command))
            return true;

        // Fallback: Check if accessible via system PATH
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = command,
                Arguments = "-version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            process?.WaitForExit();
            return process?.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
    static async Task Main()
    {
        LoadConfiguration();

        Log("=== STARTING SONG FINDER & YOUTUBE DOWNLOADER PROCESS ===");

        if (!File.Exists(Config.LinksFileName))
        {
            Log($"[INFO]: Configured links file '{Config.LinksFileName}' was missing. Created a new empty file.");
            File.WriteAllText(Config.LinksFileName, "links.txt"); // Creates an empty SongLinks.txt
            Console.WriteLine($"\nPlease add your URLs into '{Config.LinksFileName}' and run the application again.");
            return;
        }

        Directory.CreateDirectory(Config.DownloadFolderName);

        string fullPath = Path.GetFullPath(Config.LinksFileName);
        Log($"[DEBUG]: Working Directory: {Directory.GetCurrentDirectory()}");
        Log($"[DEBUG]: Full Path to Links File: {fullPath}");

        string[] urls = File.ReadAllLines(Config.LinksFileName);
        Log($"[DEBUG]: Found {urls.Length} line(s) in file.");
        using var client = new HttpClient();

        await EnsureDependenciesAsync(client);

        if (!File.Exists(Config.LinksFileName))
        {
            Log($"[INFO]: Configured links file '{Config.LinksFileName}' was missing. Created a new empty file.");
            File.WriteAllText(Config.LinksFileName, "");
            Console.WriteLine($"\nPlease add your URLs into '{Config.LinksFileName}' and run the application again.");
            return;
        }

        using var redirectHandler = new HttpClientHandler { AllowAutoRedirect = true };
        using var redirectClient = new HttpClient(redirectHandler);
        redirectClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

        TimeSpan threshold = TimeSpan.FromSeconds(Config.DurationThresholdSeconds);

        foreach (var rawUrl in urls)
        {
            string url = rawUrl?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(url)) continue;

            Log("\n--------------------------------------------------");
            Log($"[RAW URL]: {url}");

            string processedUrl = await SanitizeUrlAsync(url, redirectClient);
            if (processedUrl != url)
            {
                Log($"[RESOLVED URL]: {processedUrl}");
            }

            TimeSpan clipDuration = await GetRemoteDurationAsync(processedUrl);
            Log($"[CLIP DURATION]: {clipDuration:mm\\:ss}");

            string tempFile = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.mp3");

            try
            {
                string arguments = $"{Config.YtDlpExtraFlags} -x --audio-format mp3 -o \"{tempFile}\" \"{processedUrl}\"";
                Log($"[EXEC CLIP EXTRACTION]: yt-dlp {arguments}");

                var processInfo = new ProcessStartInfo
                {
                    FileName = "yt-dlp",
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using (var process = Process.Start(processInfo))
                {
                    string stdout = await process!.StandardOutput.ReadToEndAsync();
                    string stderr = await process.StandardError.ReadToEndAsync();
                    await process.WaitForExitAsync();

                    if (!string.IsNullOrWhiteSpace(stderr) && process.ExitCode != 0)
                        Log($"[yt-dlp STDERR]:\n{stderr.Trim()}");
                }

                if (!File.Exists(tempFile) || new FileInfo(tempFile).Length == 0)
                {
                    Log("[ERROR]: Audio extraction failed.");
                    File.AppendAllText(Config.FailedLinksFileName, $"{url} => Failed audio extraction{Environment.NewLine}");
                    continue;
                }

                Log("[AUDD REQUEST]: Uploading snippet to AudD...");
                using var form = new MultipartFormDataContent();
                using var fileStream = File.OpenRead(tempFile);
                using var streamContent = new StreamContent(fileStream);

                form.Add(new StringContent(Config.AudDApiToken), "api_token");
                form.Add(new StringContent("spotify,apple_music"), "return");
                form.Add(streamContent, "file", "audio.mp3");

                var response = await client.PostAsync("https://api.audd.io/", form);
                string jsonResponse = await response.Content.ReadAsStringAsync();

                using var doc = JsonDocument.Parse(jsonResponse);
                var root = doc.RootElement;

                // --- AUDD SUCCESS ---
                if (root.TryGetProperty("status", out var status) && status.GetString() == "success" &&
                    root.TryGetProperty("result", out var result) && result.ValueKind != JsonValueKind.Null)
                {
                    string artist = result.TryGetProperty("artist", out var a) ? a.GetString() ?? "Unknown Artist" : "Unknown Artist";
                    string title = result.TryGetProperty("title", out var t) ? t.GetString() ?? "Unknown Title" : "Unknown Title";

                    string songQuery = $"{artist} - {title}";
                    Log($"[MATCH FOUND]: {songQuery}");

                    File.AppendAllText(Config.SongNamesFileName, $"{songQuery} (Source: {url}){Environment.NewLine}");

                    Log($"[ACTION]: Searching YouTube for full track '{songQuery}'...");
                    string ytOutputTemplate = Path.Combine(Config.DownloadFolderName, "%(title)s.%(ext)s");
                    string ytArguments = $"{Config.YtDlpExtraFlags} -x --audio-format mp3 -o \"{ytOutputTemplate}\" \"ytsearch1:{songQuery}\"";

                    await RunYtDlpAsync(ytArguments);
                    Log($"[SUCCESS]: Downloaded full track from YouTube into '{Config.DownloadFolderName}/'");
                }
                // --- AUDD FAILED ---
                else
                {
                    Log("[NOT FOUND]: No match found in AudD database.");

                    if (clipDuration >= threshold)
                    {
                        Log($"[FALLBACK]: AudD failed, but clip duration ({clipDuration:mm\\:ss}) is >= {threshold:mm\\:ss}. Downloading clip audio directly from source...");

                        string fallbackOutputTemplate = Path.Combine(Config.DownloadFolderName, "%(title)s.%(ext)s");
                        string fallbackArguments = $"{Config.YtDlpExtraFlags} -x --audio-format mp3 -o \"{fallbackOutputTemplate}\" \"{processedUrl}\"";

                        await RunYtDlpAsync(fallbackArguments);
                        Log($"[SUCCESS]: Saved direct source audio into '{Config.DownloadFolderName}/'");
                    }
                    else
                    {
                        Log($"[SKIPPED]: AudD failed and clip is under {threshold:mm\\:ss}.");
                        File.AppendAllText(Config.FailedLinksFileName, $"{url} => Song not identified (Clip < {threshold:mm\\:ss}){Environment.NewLine}");
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"[EXCEPTION]: {ex.Message}");
                File.AppendAllText(Config.FailedLinksFileName, $"{url} => Exception: {ex.Message}{Environment.NewLine}");
            }
            finally
            {
                if (File.Exists(tempFile))
                {
                    File.Delete(tempFile);
                }
            }
        }

        Log("\n=== PROCESS COMPLETED ===");
    }

    private static void LoadConfiguration()
    {
        string configFile = "appsettings.json";
        if (File.Exists(configFile))
        {
            try
            {
                string json = File.ReadAllText(configFile);
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var parsedConfig = JsonSerializer.Deserialize<AppConfig>(json, options);

                if (parsedConfig != null)
                {
                    // Fall back to defaults if any string property in JSON is missing or null
                    parsedConfig.LogFileName = string.IsNullOrWhiteSpace(parsedConfig.LogFileName) ? "process.log" : parsedConfig.LogFileName;
                    parsedConfig.LinksFileName = string.IsNullOrWhiteSpace(parsedConfig.LinksFileName) ? "SongLinks.txt" : parsedConfig.LinksFileName;
                    parsedConfig.DownloadFolderName = string.IsNullOrWhiteSpace(parsedConfig.DownloadFolderName) ? "DownloadedSongs" : parsedConfig.DownloadFolderName;
                    parsedConfig.SongNamesFileName = string.IsNullOrWhiteSpace(parsedConfig.SongNamesFileName) ? "SongNames.txt" : parsedConfig.SongNamesFileName;
                    parsedConfig.FailedLinksFileName = string.IsNullOrWhiteSpace(parsedConfig.FailedLinksFileName) ? "FailedLinks.txt" : parsedConfig.FailedLinksFileName;
                    parsedConfig.AudDApiToken ??= "";
                    parsedConfig.YtDlpExtraFlags ??= "";

                    Config = parsedConfig;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WARN]: Failed to parse 'appsettings.json'. Using default settings. Error: {ex.Message}");
            }
        }
        else
        {
            // Auto-generate a default appsettings.json if it doesn't exist yet
            string defaultJson = JsonSerializer.Serialize(Config, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(configFile, defaultJson);
        }
    }

    private static void Log(string message)
    {
        string entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";
        Console.WriteLine(entry);
        File.AppendAllText(Config.LogFileName, entry + Environment.NewLine);
    }

    private static async Task<string> SanitizeUrlAsync(string inputUrl, HttpClient redirectClient)
    {
        try
        {
            if (inputUrl.Contains("tiktok.com", StringComparison.OrdinalIgnoreCase))
            {
                var response = await redirectClient.GetAsync(inputUrl, HttpCompletionOption.ResponseHeadersRead);
                string finalUrl = response.RequestMessage?.RequestUri?.ToString() ?? inputUrl;

                if (finalUrl.Contains("/photo/", StringComparison.OrdinalIgnoreCase))
                {
                    finalUrl = finalUrl.Replace("/photo/", "/video/", StringComparison.OrdinalIgnoreCase);
                }

                return finalUrl;
            }

            return inputUrl;
        }
        catch
        {
            return inputUrl;
        }
    }

    private static async Task<TimeSpan> GetRemoteDurationAsync(string url)
    {
        try
        {
            var processInfo = new ProcessStartInfo
            {
                FileName = "yt-dlp",
                Arguments = $"{Config.YtDlpExtraFlags} --print duration \"{url}\"",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(processInfo);
            string output = await process!.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (double.TryParse(output.Trim(), System.Globalization.CultureInfo.InvariantCulture, out double seconds))
            {
                return TimeSpan.FromSeconds(seconds);
            }
        }
        catch
        {
            // Fallback if metadata lookup fails
        }

        return TimeSpan.Zero;
    }

    private static async Task RunYtDlpAsync(string arguments)
    {
        var processInfo = new ProcessStartInfo
        {
            FileName = "yt-dlp",
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(processInfo);
        string stderr = await process!.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0 && !string.IsNullOrWhiteSpace(stderr))
        {
            Log($"[yt-dlp ERROR]: {stderr.Trim()}");
        }
    }


}