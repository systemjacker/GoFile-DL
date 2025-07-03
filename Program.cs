﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;

namespace GoFile_DL
{
    
    public static class Logger
    {
        public enum LogLevel { INFO, ERROR }

        public static void Log(LogLevel level, string message, [System.Runtime.CompilerServices.CallerMemberName] string memberName = "")
        {
            
            if (level == LogLevel.ERROR)
            {
                
                ConsoleUI.ClearCurrentLine();
                Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}][{memberName,-20}()][{level,-8}]: {message}");
                Program.ConsoleUIRef?.RedrawAll(); 
            }
        }
    }

    
    public class File
    {
        public string Link { get; }
        public string Destination { get; }

        public File(string link, string dest)
        {
            Link = link;
            Destination = dest;
        }

        public override string ToString()
        {
            return $"{Destination} ({Link})";
        }
    }

    
    public class Downloader
    {
        private readonly string _token;
        private readonly SemaphoreSlim _progressLock = new SemaphoreSlim(1, 1); 
        private DownloadProgressDisplay? _progressDisplay; 
        private readonly ConsoleUI _consoleUI; 

        public Downloader(string token, ConsoleUI consoleUI)
        {
            _token = token;
            _consoleUI = consoleUI;
        }

        
        private async Task<(long totalSize, bool isSupportRange)> GetTotalSizeAsync(string link)
        {
            using (var client = new HttpClient())
            {
                client.DefaultRequestHeaders.Add("Cookie", $"accountToken={_token}");
                var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, link));
                response.EnsureSuccessStatusCode();

                long totalSize = response.Content.Headers.ContentLength ?? 0;
                bool isSupportRange = response.Headers.AcceptRanges.Contains("bytes");
                return (totalSize, isSupportRange);
            }
        }

        
        private async Task DownloadRangeAsync(string link, long start, long end, string tempFile, int partIndex)
        {
            long existingSize = 0;
            if (System.IO.File.Exists(tempFile))
            {
                existingSize = new FileInfo(tempFile).Length;
            }

            long rangeStart = start + existingSize;
            if (rangeStart > end)
            {
                return; 
            }

            using (var client = new HttpClient())
            {
                client.DefaultRequestHeaders.Add("Cookie", $"accountToken={_token}");
                client.DefaultRequestHeaders.Add("Range", $"bytes={rangeStart}-{end}");

                using (var response = await client.GetAsync(link, HttpCompletionOption.ResponseHeadersRead))
                {
                    response.EnsureSuccessStatusCode();
                    using (var contentStream = await response.Content.ReadAsStreamAsync())
                    {
                        using (var fileStream = new FileStream(tempFile, FileMode.Append, FileAccess.Write, FileShare.None, 8192, useAsync: true))
                        {
                            var buffer = new byte[8192];
                            int bytesRead;
                            while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                            {
                                await fileStream.WriteAsync(buffer, 0, bytesRead);
                                await _progressLock.WaitAsync();
                                try
                                {
                                    _progressDisplay?.Update(bytesRead);
                                }
                                finally
                                {
                                    _progressLock.Release();
                                }
                            }
                        }
                    }
                }
            }
        }

        
        private void MergeTempFiles(string tempDir, string dest, int numThreads)
        {
            using (var outputStream = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                for (int i = 0; i < numThreads; i++)
                {
                    string tempFile = Path.Combine(tempDir, $"part_{i}");
                    if (System.IO.File.Exists(tempFile))
                    {
                        using (var inputStream = new FileStream(tempFile, FileMode.Open, FileAccess.Read, FileShare.Read))
                        {
                            inputStream.CopyTo(outputStream);
                        }
                        System.IO.File.Delete(tempFile);
                    }
                }
            }
            Directory.Delete(tempDir, true); 
        }

        
        public async Task DownloadAsync(File file, int numThreads = 1)
        {
            string link = file.Link;
            string dest = file.Destination;
            string tempDir = dest + "_parts"; 

            try
            {
                var (totalSize, isSupportRange) = await GetTotalSizeAsync(link);

                
                if (System.IO.File.Exists(dest))
                {
                    if (new FileInfo(dest).Length == totalSize)
                    {
                        _consoleUI.AddOrUpdateDownloadStatus(Path.GetFileName(dest), "Already downloaded", 1.0, 0, 0);
                        return;
                    }
                }

                
                string? destinationDirectory = Path.GetDirectoryName(dest); 
                if (!string.IsNullOrEmpty(destinationDirectory) && !Directory.Exists(destinationDirectory))
                {
                    try
                    {
                        Directory.CreateDirectory(destinationDirectory);
                    }
                    catch (Exception dirEx)
                    {
                        Logger.Log(Logger.LogLevel.ERROR, $"Failed to create directory '{destinationDirectory}': {dirEx.Message}");
                        return;
                    }
                }


                if (numThreads == 1 || !isSupportRange)
                {
                    string singleTempFile = dest + ".part";
                    long downloadedBytes = 0;
                    if (System.IO.File.Exists(singleTempFile))
                    {
                        downloadedBytes = new FileInfo(singleTempFile).Length;
                    }

                    _progressDisplay = new DownloadProgressDisplay(totalSize, downloadedBytes, Path.GetFileName(dest), "Downloading", _consoleUI);
                    _consoleUI.AddDownload(_progressDisplay);
                    _progressDisplay.Start();

                    using (var client = new HttpClient())
                    {
                        client.DefaultRequestHeaders.Add("Cookie", $"accountToken={_token}");
                        if (downloadedBytes > 0)
                        {
                            client.DefaultRequestHeaders.Add("Range", $"bytes={downloadedBytes}-");
                        }

                        using (var response = await client.GetAsync(link, HttpCompletionOption.ResponseHeadersRead))
                        {
                            response.EnsureSuccessStatusCode();
                            using (var contentStream = await response.Content.ReadAsStreamAsync())
                            {
                                using (var fileStream = new FileStream(singleTempFile, FileMode.Append, FileAccess.Write, FileShare.None, 8192, useAsync: true))
                                {
                                    var buffer = new byte[8192];
                                    int bytesRead;
                                    while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                                    {
                                        await fileStream.WriteAsync(buffer, 0, bytesRead);
                                        _progressDisplay.Update(len: bytesRead);
                                    }
                                }
                            }
                        }
                    }

                    _progressDisplay.SetStatus("Completed");
                    _progressDisplay.Stop();
                    System.IO.File.Move(singleTempFile, dest, true);
                }
                else
                {
                    
                    string singleTempFile = dest + ".part";
                    if (System.IO.File.Exists(singleTempFile))
                    {
                        System.IO.File.Delete(singleTempFile);
                    }

                    
                    string numThreadsCheckFile = Path.Combine(tempDir, "num_threads");
                    if (Directory.Exists(tempDir))
                    {
                        int prevNumThreads = 0;
                        if (System.IO.File.Exists(numThreadsCheckFile))
                        {
                            if (int.TryParse(System.IO.File.ReadAllText(numThreadsCheckFile), out int parsedThreads))
                            {
                                prevNumThreads = parsedThreads;
                            }
                        }

                        if (prevNumThreads != numThreads)
                        {
                            _consoleUI.AddOrUpdateDownloadStatus(Path.GetFileName(dest), "Threads changed, clearing parts", 0.0, 0, 0);
                            Directory.Delete(tempDir, true);
                        }
                    }

                    if (!Directory.Exists(tempDir))
                    {
                        try
                        {
                            Directory.CreateDirectory(tempDir);
                            System.IO.File.WriteAllText(numThreadsCheckFile, numThreads.ToString());
                        }
                        catch (Exception dirEx)
                        {
                            Logger.Log(Logger.LogLevel.ERROR, $"Failed to create temporary directory '{tempDir}': {dirEx.Message}");
                            return; 
                        }
                    }

                    long partSize = (long)Math.Ceiling((double)totalSize / numThreads);
                    long downloadedBytes = 0;
                    for (int i = 0; i < numThreads; i++)
                    {
                        string partFile = Path.Combine(tempDir, $"part_{i}");
                        if (System.IO.File.Exists(partFile))
                        {
                            downloadedBytes += new FileInfo(partFile).Length;
                        }
                    }

                    _progressDisplay = new DownloadProgressDisplay(totalSize, downloadedBytes, Path.GetFileName(dest), "Downloading", _consoleUI);
                    _consoleUI.AddDownload(_progressDisplay);
                    _progressDisplay.Start();

                    var tasks = new List<Task>();
                    for (int i = 0; i < numThreads; i++)
                    {
                        long start = i * partSize;
                        long end = Math.Min(start + partSize - 1, totalSize - 1);
                        string tempPartFile = Path.Combine(tempDir, $"part_{i}");
                        tasks.Add(DownloadRangeAsync(link, start, end, tempPartFile, i));
                    }

                    await Task.WhenAll(tasks);

                    _progressDisplay.SetStatus("Merging");
                    MergeTempFiles(tempDir, dest, numThreads);
                    _progressDisplay.SetStatus("Completed");
                    _progressDisplay.Stop();
                }
            }
            catch (HttpRequestException httpEx)
            {
                _progressDisplay?.Stop();
                Logger.Log(Logger.LogLevel.ERROR, $"HTTP error downloading {dest} ({link}): {httpEx.Message}");
            }
            catch (Exception ex)
            {
                _progressDisplay?.Stop();
                Logger.Log(Logger.LogLevel.ERROR, $"Failed to download {dest} ({link}): {ex.Message}");
            }
        }
    }

    
    public class DownloadProgressDisplay : IDisposable
    {
        private const int BlockCount = 20;
        private const char FullBlock = '█';
        private const char EmptyBlock = '░';
        private readonly TimeSpan AnimationInterval = TimeSpan.FromSeconds(1.0 / 8);
        private readonly Timer _timer;
        private long _total;
        private long _current;
        private string _filename;
        private string _status;
        private int _animationFrame;
        private bool _started;
        private bool _disposed = false;
        private DateTime _startTime;
        private long _lastBytes = 0;
        private DateTime _lastTime = DateTime.MinValue;
        private double _currentSpeed = 0;
        private readonly ConsoleUI _consoleUI;
        private int _displayLineIndex;

        public string Filename => _filename;
        public string Status => _status;
        public double Progress => _total == 0 ? 0.0 : (double)_current / _total;
        public long CurrentBytes => _current;
        public long TotalBytes => _total;
        public double CurrentSpeed => _currentSpeed;
        public int DisplayLineIndex { get => _displayLineIndex; set => _displayLineIndex = value; }

        public DownloadProgressDisplay(long total, long initial, string filename, string initialStatus, ConsoleUI consoleUI)
        {
            _total = total;
            _current = initial;
            _filename = filename;
            _status = initialStatus;
            _consoleUI = consoleUI;
            _timer = new Timer(TimerHandler!);
        }

        public void Start()
        {
            if (!_started)
            {
                _started = true;
                _startTime = DateTime.Now;
                _lastTime = DateTime.Now;
                _lastBytes = _current;
                ResetTimer();
                _consoleUI.RedrawDownloadArea();
            }
        }

        public void Stop()
        {
            if (_started)
            {
                _started = false;
                _timer.Change(Timeout.Infinite, Timeout.Infinite);
                _consoleUI.RedrawDownloadArea();
            }
        }

        public void Update(long len)
        {
            Interlocked.Add(ref _current, len);
            _consoleUI.RedrawDownloadArea();
        }

        public void SetStatus(string newStatus)
        {
            _status = newStatus;
            _consoleUI.RedrawDownloadArea();
        }

        private void TimerHandler(object? state)
        {
            lock (this)
            {
                if (_disposed || !_started) return;

                _animationFrame++;
                UpdateSpeed();
                _consoleUI.RedrawDownloadArea();
            }
        }

        private void UpdateSpeed()
        {
            DateTime now = DateTime.Now;
            TimeSpan elapsed = now - _lastTime;

            if (elapsed.TotalSeconds >= 0.5)
            {
                long bytesSinceLastUpdate = _current - _lastBytes;
                _currentSpeed = bytesSinceLastUpdate / elapsed.TotalSeconds;
                _lastBytes = _current;
                _lastTime = now;
            }
        }

        public string GetProgressBarString()
        {
            int progressBlocks = (int)Math.Round(Progress * BlockCount);
            return $"[{new string(FullBlock, progressBlocks)}{new string(EmptyBlock, BlockCount - progressBlocks)}]";
        }

        public string GetSpinnerFrame()
        {
            string[] spinnerFrames = { "|", "/", "-", "\\" };
            return spinnerFrames[_animationFrame % spinnerFrames.Length];
        }

        private void ResetTimer()
        {
            _timer.Change(AnimationInterval, AnimationInterval);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed) return;

            if (disposing)
            {
                _timer?.Dispose();
            }

            _disposed = true;
        }
    }

    
    public class ConsoleUI : IDisposable
    {
        private const int HEADER_HEIGHT = 5; 
        private const int FOOTER_HEIGHT = 1; 
        private string _token = string.Empty;
        private string _websiteToken = string.Empty;
        
        private List<DownloadProgressDisplay> _activeDownloads = new List<DownloadProgressDisplay>();
        private readonly ConcurrentDictionary<string, DownloadProgressDisplay> _downloadMap = new ConcurrentDictionary<string, DownloadProgressDisplay>(); // For quick lookup
        private Timer _resizeMonitorTimer;
        private int _lastWindowWidth;
        private int _lastWindowHeight;
        private static readonly object _consoleLock = new object();

        public ConsoleUI()
        {
            Console.CursorVisible = false;
            _lastWindowWidth = Console.WindowWidth;
            _lastWindowHeight = Console.WindowHeight;
            _resizeMonitorTimer = new Timer(CheckForResize!, null, TimeSpan.Zero, TimeSpan.FromMilliseconds(500));
        }

        public void SetTokens(string token, string websiteToken)
        {
            _token = token;
            _websiteToken = websiteToken;
            RedrawHeader();
        }

        public void AddDownload(DownloadProgressDisplay download)
        {
            
            if (_downloadMap.TryAdd(download.Filename, download))
            {
                lock (_activeDownloads)
                {
                    _activeDownloads.Add(download);
                }
            }
            RedrawDownloadArea();
        }

        public void AddOrUpdateDownloadStatus(string filename, string status, double progress, long currentBytes, long totalBytes)
        {
            
            lock (_consoleLock)
            {
                
                Console.SetCursorPosition(0, Console.WindowHeight - FOOTER_HEIGHT);
                ClearCurrentLine();
                Console.WriteLine($"Filename: {filename} | Status: {status}");
            }
            RedrawAll();
        }


        private void CheckForResize(object? state) 
        {
            if (Console.WindowWidth != _lastWindowWidth || Console.WindowHeight != _lastWindowHeight)
            {
                _lastWindowWidth = Console.WindowWidth;
                _lastWindowHeight = Console.WindowHeight;
                RedrawAll();
            }
        }

        public static void ClearCurrentLine()
        {
            lock (_consoleLock)
            {
                int currentLineCursorTop = Console.CursorTop;
                int currentLineCursorLeft = Console.CursorLeft;
                Console.SetCursorPosition(0, currentLineCursorTop);
                Console.Write(new string(' ', Console.WindowWidth - 1));
                Console.SetCursorPosition(0, currentLineCursorTop);
            }
        }

        public void RedrawAll()
        {
            lock (_consoleLock)
            {
                Console.Clear();
                RedrawHeader();
                RedrawDownloadArea();
                
            }
        }

        public void RedrawHeader()
        {
            lock (_consoleLock)
            {
                int originalCursorTop = Console.CursorTop;
                int originalCursorLeft = Console.CursorLeft;

                Console.SetCursorPosition(0, 0);
                DrawHorizontalLine();
                DrawCenteredText("GoFile-DL", 1);
                DrawHorizontalLine();

                int tokenLine = 3;
                int websiteTokenLine = 4;

                
                Console.SetCursorPosition(0, tokenLine);
                Console.Write(new string(' ', Console.WindowWidth - 1));
                Console.SetCursorPosition(0, websiteTokenLine);
                Console.Write(new string(' ', Console.WindowWidth - 1));

                Console.SetCursorPosition(0, tokenLine);
                Console.Write($"Token: {_token}");

                Console.SetCursorPosition(Console.WindowWidth / 2, tokenLine);
                Console.Write($"WebsiteToken: {_websiteToken}");

                Console.SetCursorPosition(0, websiteTokenLine);
                DrawHorizontalLine();

                Console.SetCursorPosition(originalCursorLeft, originalCursorTop); 
            }
        }

        public void RedrawDownloadArea()
        {
            lock (_consoleLock)
            {
                int startLine = HEADER_HEIGHT;
                int maxLines = Console.WindowHeight - HEADER_HEIGHT - FOOTER_HEIGHT;

                
                var sortedDownloads = _activeDownloads
                    .OrderBy(d => d.Status == "Completed" ? 1 : 0) 
                    .ThenBy(d => d.Status == "Downloading" ? 0 : 1)
                    .ThenBy(d => d.Filename)
                    .ToList();

                for (int i = 0; i < maxLines; i++)
                {
                    Console.SetCursorPosition(0, startLine + i);
                    ClearCurrentLine();

                    if (i < sortedDownloads.Count)
                    {
                        var download = sortedDownloads[i];
                        

                        string filenameDisplay = download.Filename;
                        if (filenameDisplay.Length > 30)
                        {
                            filenameDisplay = filenameDisplay.Substring(0, 12) + "..." + filenameDisplay.Substring(filenameDisplay.Length - 15);
                        }
                        filenameDisplay = filenameDisplay.PadRight(30);

                        string statusDisplay = download.Status.PadRight(12);

                        string fullMessage = $"Filename: {filenameDisplay} | Status: {statusDisplay} | Progress: {download.Progress * 100:0.00}% {download.GetProgressBarString()} | {download.CurrentBytes.ToHumanReadableSize()}/{download.TotalBytes.ToHumanReadableSize()} | Speed: {download.CurrentSpeed.ToHumanReadableSpeed()} {download.GetSpinnerFrame()}";

                        if (fullMessage.Length >= Console.WindowWidth)
                        {
                            fullMessage = fullMessage.Substring(0, Console.WindowWidth - 1);
                        }
                        Console.Write(fullMessage);
                    }
                }
                
                Console.SetCursorPosition(0, Math.Min(startLine + sortedDownloads.Count, Console.WindowHeight - FOOTER_HEIGHT));
            }
        }

        private void DrawHorizontalLine()
        {
            Console.WriteLine(new string('═', Console.WindowWidth));
        }

        private void DrawCenteredText(string text, int line)
        {
            int originalCursorTop = Console.CursorTop;
            int originalCursorLeft = Console.CursorLeft;

            Console.SetCursorPosition(0, line);
            int padding = (Console.WindowWidth - text.Length) / 2;
            Console.WriteLine($"{new string(' ', padding)}{text}{new string(' ', Console.WindowWidth - padding - text.Length)}");

            Console.SetCursorPosition(originalCursorLeft, originalCursorTop);
        }

        public void Dispose()
        {
            _resizeMonitorTimer?.Dispose();
            Console.CursorVisible = true;
            Console.Clear();
        }
    }

    
    public static class LongExtensions
    {
        public static string ToHumanReadableSize(this long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            int order = 0;
            double len = bytes;
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len /= 1024;
            }
            return $"{len:0.##} {sizes[order]}";
        }

        public static string ToHumanReadableSpeed(this double bytesPerSecond)
        {
            string[] sizes = { "B/s", "KB/s", "MB/s", "GB/s", "TB/s" };
            int order = 0;
            double speed = bytesPerSecond;
            while (speed >= 1024 && order < sizes.Length - 1)
            {
                order++;
                speed /= 1024;
            }
            return $"{speed:0.##} {sizes[order]}";
        }
    }

    
    public static class Utils
    {
        
        public static string SanitizeFilename(string filename)
        {
            
            char[] invalidChars = Path.GetInvalidPathChars();
            string invalidReStr = string.Format(@"[{0}]", Regex.Escape(new string(invalidChars)));
            
            return Regex.Replace(filename, invalidReStr, "_").Trim();
        }

        
        public static string ComputeSha256Hash(string rawData)
        {
            using (SHA256 sha256Hash = SHA256.Create())
            {
                byte[] bytes = sha256Hash.ComputeHash(Encoding.UTF8.GetBytes(rawData));
                StringBuilder builder = new StringBuilder();
                for (int i = 0; i < bytes.Length; i++)
                {
                    builder.Append(bytes[i].ToString("x2"));
                }
                return builder.ToString();
            }
        }
    }

    
    public class GoFile
    {
        private static GoFile? _instance;
        private static readonly object _lock = new object();

        private string _token = string.Empty; 
        private string _websiteToken = string.Empty;
        private readonly HttpClient _httpClient;
        private readonly ConsoleUI _consoleUI;

        
        private GoFile(ConsoleUI consoleUI)
        {
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "GoFile-DL-C#App/1.0");
            _consoleUI = consoleUI;
        }

        
        public static GoFile Instance(ConsoleUI consoleUI)
        {
            if (_instance == null)
            {
                lock (_lock)
                {
                    _instance ??= new GoFile(consoleUI);
                }
            }
            return _instance;
        }

        
        private async Task UpdateTokenAsync()
        {
            if (string.IsNullOrEmpty(_token))
            {
                try
                {
                    var response = await _httpClient.PostAsync("https://api.gofile.io/accounts", null);
                    response.EnsureSuccessStatusCode();
                    var data = await response.Content.ReadAsAsync<dynamic>();
                    if (data.status == "ok")
                    {
                        _token = data.data.token;
                        _consoleUI.SetTokens(_token, _websiteToken);
                    }
                    else
                    {
                        throw new Exception("Cannot get token from GoFile API.");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log(Logger.LogLevel.ERROR, $"Error updating token: {ex.Message}");
                    throw;
                }
            }
        }

        
        private async Task UpdateWebsiteTokenAsync()
        {
            if (string.IsNullOrEmpty(_websiteToken))
            {
                try
                {
                    var response = await _httpClient.GetStringAsync("https://gofile.io/dist/js/global.js");
                    var match = Regex.Match(response, @"appdata\.wt = ""(?<wt>[^""]+)""");
                    if (match.Success)
                    {
                        _websiteToken = match.Groups["wt"].Value;
                        _consoleUI.SetTokens(_token, _websiteToken);
                    }
                    else
                    {
                        throw new Exception("Cannot get 'websiteToken' parameter from global.js.");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log(Logger.LogLevel.ERROR, $"Error updating WebsiteToken: {ex.Message}");
                    throw;
                }
            }
        }

        
        public async Task<List<File>> GetFilesAsync(string outputDir, string? contentId = null, string? url = null, string? password = null, List<string>? excludes = null)
        {
            excludes ??= new List<string>();
            var files = new List<File>();

            if (!string.IsNullOrEmpty(contentId))
            {
                await UpdateTokenAsync();
                await UpdateWebsiteTokenAsync();

                string hashPassword = string.IsNullOrEmpty(password) ? "" : Utils.ComputeSha256Hash(password);
                string apiUrl = $"https://api.gofile.io/contents/{contentId}?wt={_websiteToken}&cache=true&password={hashPassword}";

                _httpClient.DefaultRequestHeaders.Remove("Authorization");
                _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {_token}");

                try
                {
                    var response = await _httpClient.GetAsync(apiUrl);
                    response.EnsureSuccessStatusCode();
                    var data = await response.Content.ReadAsAsync<dynamic>();

                    if (data.status == "ok")
                    {
                        string passwordStatus = data.data.passwordStatus ?? "passwordOk";
                        if (passwordStatus == "passwordOk")
                        {
                            if (data.data.type == "folder")
                            {
                                string folderName = data.data.name;
                                string currentOutputDir = Path.Combine(outputDir, Utils.SanitizeFilename(folderName));

                                foreach (var child in data.data.children)
                                {
                                    string childId = child.Name; 
                                    var childData = child.Value;

                                    if (childData.type == "folder")
                                    {
                                        var folderFiles = await GetFilesAsync(currentOutputDir, contentId: childId, password: password, excludes: excludes);
                                        files.AddRange(folderFiles);
                                    }
                                    else
                                    {
                                        string filename = childData.name;
                                        if (!excludes.Any(pattern => IsMatch(filename, pattern)))
                                        {
                                            
                                            files.Add(new File(Uri.UnescapeDataString(childData.link.ToString()), Path.Combine(currentOutputDir, Utils.SanitizeFilename(filename)).Trim()));
                                        }
                                    }
                                }
                            }
                            else
                            {
                                string filename = data.data.name;
                                if (!excludes.Any(pattern => IsMatch(filename, pattern)))
                                {
                                    files.Add(new File(Uri.UnescapeDataString(data.data.link.ToString()), Path.Combine(outputDir, Utils.SanitizeFilename(filename)).Trim()));
                                }
                            }
                        }
                        else
                        {
                            Logger.Log(Logger.LogLevel.ERROR, $"Invalid password: {passwordStatus}");
                        }
                    }
                    else
                    {
                        Logger.Log(Logger.LogLevel.ERROR, $"GoFile API error: {data.status} - {data.message}");
                    }
                }
                catch (HttpRequestException httpEx)
                {
                    Logger.Log(Logger.LogLevel.ERROR, $"HTTP error getting files for content ID {contentId}: {httpEx.Message}");
                }
                catch (Exception ex)
                {
                    Logger.Log(Logger.LogLevel.ERROR, $"Error getting files for content ID {contentId}: {ex.Message}");
                }
            }
            else if (!string.IsNullOrEmpty(url))
            {
                if (url.StartsWith("https://gofile.io/d/"))
                {
                    string extractedContentId = url.Split('/').Last();
                    files.AddRange(await GetFilesAsync(outputDir, contentId: extractedContentId, password: password, excludes: excludes));
                }
                else
                {
                    Logger.Log(Logger.LogLevel.ERROR, $"Invalid URL format: {url}");
                }
            }
            else
            {
                Logger.Log(Logger.LogLevel.ERROR, "Invalid parameters: Either content ID or URL must be provided.");
            }

            return files;
        }

        
        private bool IsMatch(string filename, string pattern)
        {
            
            string regexPattern = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
            return Regex.IsMatch(filename, regexPattern, RegexOptions.IgnoreCase);
        }

        
        public async Task ExecuteAsync(string outputDir, string? contentId = null, string? url = null, string? password = null, int numThreads = 1, List<string>? excludes = null) 
        {
            _consoleUI.RedrawHeader();

            var filesToDownload = await GetFilesAsync(outputDir, contentId, url, password, excludes);

            if (!filesToDownload.Any())
            {
                _consoleUI.AddOrUpdateDownloadStatus("N/A", "No files found", 0, 0, 0);
                return;
            }

            foreach (var file in filesToDownload)
            {
                await new Downloader(_token, _consoleUI).DownloadAsync(file, numThreads);
            }
            _consoleUI.AddOrUpdateDownloadStatus("N/A", "All downloads completed", 1.0, 0, 0);
        }
    }

    
    class Program
    {
        public static ConsoleUI ConsoleUIRef = null!;

        static async Task Main(string[] args)
        {
            ConsoleUIRef = new ConsoleUI();
            Console.CancelKeyPress += (sender, e) =>
            {
                ConsoleUIRef.Dispose();
            };

            string? url = null;
            int numThreads = 1;
            string outputDir = "./output";
            string? password = null;
            List<string>? excludes = new List<string>();

            
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "-t":
                        if (i + 1 < args.Length && int.TryParse(args[i + 1], out int threads))
                        {
                            numThreads = threads;
                            i++;
                        }
                        else
                        {
                            Logger.Log(Logger.LogLevel.ERROR, "Invalid value for -t (num_threads). Must be an integer.");
                            ConsoleUIRef.Dispose(); return;
                        }
                        break;
                    case "-d":
                        if (i + 1 < args.Length)
                        {
                            outputDir = args[i + 1];
                            i++;
                        }
                        else
                        {
                            Logger.Log(Logger.LogLevel.ERROR, "Missing value for -d (output directory).");
                            ConsoleUIRef.Dispose(); return;
                        }
                        break;
                    case "-p":
                        if (i + 1 < args.Length)
                        {
                            password = args[i + 1];
                            i++;
                        }
                        else
                        {
                            Logger.Log(Logger.LogLevel.ERROR, "Missing value for -p (password).");
                            ConsoleUIRef.Dispose(); return;
                        }
                        break;
                    case "-e":
                        if (i + 1 < args.Length)
                        {
                            excludes.Add(args[i + 1]);
                            i++;
                        }
                        else
                        {
                            Logger.Log(Logger.LogLevel.ERROR, "Missing value for -e (exclude pattern).");
                            ConsoleUIRef.Dispose(); return;
                        }
                        break;
                    default:
                        if (args[i].StartsWith("-"))
                        {
                            Logger.Log(Logger.LogLevel.ERROR, $"Unknown argument: {args[i]}");
                            ConsoleUIRef.Dispose(); return;
                        }
                        else if (url == null)
                        {
                            url = args[i];
                        }
                        else
                        {
                            Logger.Log(Logger.LogLevel.ERROR, $"Multiple URLs provided or unexpected argument: {args[i]}");
                            ConsoleUIRef.Dispose(); return;
                        }
                        break;
                }
            }

            if (string.IsNullOrEmpty(url))
            {
                Logger.Log(Logger.LogLevel.ERROR, "Usage: GoFile-DL <URL_or_ContentID> [-t <num_threads>] [-d <output_directory>] [-p <password>] [-e <exclude_pattern>]");
                ConsoleUIRef.Dispose(); return;
            }

            
            string? contentId = null;
            if (!url.StartsWith("https://gofile.io/d/"))
            {
                
                contentId = url;
                url = null;
            }

            
            if (!Directory.Exists(outputDir))
            {
                try
                {
                    Directory.CreateDirectory(outputDir);
                }
                catch (Exception ex)
                {
                    Logger.Log(Logger.LogLevel.ERROR, $"Failed to create base output directory '{outputDir}': {ex.Message}");
                    ConsoleUIRef.Dispose(); return;
                }
            }

            await GoFile.Instance(ConsoleUIRef).ExecuteAsync(outputDir, contentId, url, password, numThreads, excludes);

            ConsoleUIRef.Dispose();
        }
    }
}