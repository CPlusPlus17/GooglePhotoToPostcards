using System.Diagnostics;
using CasCap.Models;
using CasCap.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net;
using System.Text.RegularExpressions;
using Serilog;
using Serilog.Context;

// Logging stuff
var serilog =  new LoggerConfiguration()
    .MinimumLevel.Verbose()
    .Enrich.FromLogContext()
    .WriteTo.Console(outputTemplate:"[{Timestamp:yyyy-MM-dd HH:mm:ss.fff} {Level:u3}] {Method} - {Message:l}{NewLine}{Exception}")
    .CreateLogger();

var loggerFactory = new LoggerFactory().AddSerilog(serilog);

// Run
Task.WaitAll(SyncPhotos(loggerFactory), SendPostcard(loggerFactory));
return;

async Task SyncPhotos(ILoggerFactory factory)
{
    LogContext.PushProperty("Method", nameof(SyncPhotos));
    var logger = factory.CreateLogger<Program>();
    var loggerGoogleSvc = factory.CreateLogger<GooglePhotosService>();

    // Envs
    var envUser = Environment.GetEnvironmentVariable("GPSC_USER");
    var envClientId = Environment.GetEnvironmentVariable("GPSC_CLIENTID");
    var envClientSecret = Environment.GetEnvironmentVariable("GPSC_CLIENTSECRET");
    var envMediaFolderPath = Environment.GetEnvironmentVariable("GPSC_MEDIAFOLDERPATH");
    var envAlbumsToSync = Environment.GetEnvironmentVariable("GPSC_ALBUMSTOSYNC");
    var envSyncedIdsFilePath = Environment.GetEnvironmentVariable("GPSC_SYNCEDIDSFILEPATH");
    var envTimeBetweenSyncsInMinutes = Environment.GetEnvironmentVariable("GPSC_TIMEBETWEENMINUTES");
    var envConfigPath = Environment.GetEnvironmentVariable("GPSC_CONFIGPATH");

    // Validate all envs are present
    if (string.IsNullOrWhiteSpace(envUser)
        || string.IsNullOrWhiteSpace(envClientId)
        || string.IsNullOrWhiteSpace(envClientSecret)
        || string.IsNullOrWhiteSpace(envMediaFolderPath)
        || string.IsNullOrWhiteSpace(envAlbumsToSync)
        || string.IsNullOrWhiteSpace(envSyncedIdsFilePath)
        || !int.TryParse(envTimeBetweenSyncsInMinutes, out var timeBetweenSyncsInMinutes)
        || string.IsNullOrWhiteSpace(envConfigPath))
    {
        logger.LogError("Not all GPSC enviroment arguments are present GPSC_USER/{User}, " +
                        "GPSC_CLIENTID/{ClientId}, GPSC_CLIENTSECRET/***, GPSC_MEDIAFOLDERPATH/{MediaPath}, " +
                        "GPSC_ALBUMSTOSYNC/{SyncAlbums}, GPSC_SYNCEDIDSFILEPATH/{SyncIdPath}, " +
                        "GPSC_CONFIGPATH/{ConfigPath}",
            envUser,
            envClientId,
            envMediaFolderPath,
            envAlbumsToSync,
            envSyncedIdsFilePath,
            envConfigPath);
        return;
    }

    // Create file if missing
    if (!File.Exists(envSyncedIdsFilePath))
    {
        if (!Directory.Exists(Path.GetDirectoryName(envSyncedIdsFilePath))) Directory.CreateDirectory(envSyncedIdsFilePath);
        await using (File.Create(envSyncedIdsFilePath)) { }
    }

    // Get ids from file
    var syncedIds = (await File.ReadAllLinesAsync(envSyncedIdsFilePath)).ToList();

    // Check if media folder exists, else create
    if (!Directory.Exists(envMediaFolderPath))
    {
        Console.WriteLine($"Cannot find folder '{envMediaFolderPath}', creating it");
        Directory.CreateDirectory(envMediaFolderPath);
    }

    // Create options for google service
    var options = new GooglePhotosOptions
    {
        User = envUser,
        ClientId = envClientId,
        ClientSecret = envClientSecret,
        Scopes = new[] {GooglePhotosScope.Access},
        FileDataStoreFullPathOverride = envConfigPath
    };

    // Http handler and client for service
    var handler = new HttpClientHandler {AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate};
    var client = new HttpClient(handler) {BaseAddress = new Uri(options.BaseAddress)};

    // Google photo service
    var googlePhotosSvc = new GooglePhotosService(loggerGoogleSvc, Options.Create(options), client);

    // Try to login
    do
    {
        if (!await googlePhotosSvc.LoginAsync()) throw new("login failed!");

        // For each album do sync
        foreach (var albumTitle in envAlbumsToSync.Split(","))
        {
            // Try to get album
            var album = await googlePhotosSvc.GetAlbumByTitleAsync(albumTitle);
            if (album is null)
            {
                logger.LogWarning("Album {AlbumTitle} not found, creating it", albumTitle);
                album = await googlePhotosSvc.CreateAlbumAsync(albumTitle);

                if (album is null) continue;
            }

            // Sync content found in album
            await foreach (var item in googlePhotosSvc.GetMediaItemsByAlbumAsync(album.id))
            {
                // Only sync new files
                if (syncedIds.Contains(item.id))
                {
                    logger.LogWarning("Item already synced {ItemName}", item.filename);
                    continue;
                }

                logger.LogInformation("Downloading {ItemName}", item.filename);

                var bytes = await googlePhotosSvc.DownloadBytes(item);
                if (bytes is null)
                {
                    logger.LogError("Downloaded item has 0 bytes, skip saving it");
                    continue;
                }

                File.WriteAllBytes(Path.Combine(envMediaFolderPath, item.filename), bytes);

                // Append new id
                syncedIds.Add(item.id);
                await File.AppendAllLinesAsync(envSyncedIdsFilePath, new[] {item.id});
            }
        }

        // Wait for next sync
        logger.LogInformation("Waiting for {Minutes} minutes until next sync", timeBetweenSyncsInMinutes);
        await Task.Delay((int) TimeSpan.FromMinutes(timeBetweenSyncsInMinutes).TotalMilliseconds);
    } while (true);
}

async Task SendPostcard(ILoggerFactory factory)
{
    LogContext.PushProperty("Method", nameof(SendPostcard));
    var logger = factory.CreateLogger<Program>();
    var envMediaFolderPath = Environment.GetEnvironmentVariable("GPSC_MEDIAFOLDERPATH");
    var timeToDelayNextTry = (int) TimeSpan.FromMinutes(24 * 60 + 1).TotalMilliseconds;

    // Validate all envs are present
    if (string.IsNullOrWhiteSpace(envMediaFolderPath))
    {
        logger.LogError("Not all GPSC enviroment arguments are present GPSC_MEDIAFOLDERPATH/{MediaPath}",
            envMediaFolderPath);
        return;
    }

    do
    {
        var fileToSend = new DirectoryInfo(envMediaFolderPath).GetFiles()
            .OrderBy(f => f.LastWriteTime)
            .Select(f => f.Name)
            .ToList()
            .First();

        try
        {
            ProcessStartInfo startInfo = new()
            {
                FileName = "postcards",
                Arguments = $"send --config /config.json --picture {Path.Combine(envMediaFolderPath, fileToSend)}",
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            var proc = Process.Start(startInfo);
            ArgumentNullException.ThrowIfNull(proc);
            var output = proc.StandardOutput.ReadToEnd();
            await proc.WaitForExitAsync();

            logger.LogInformation("Sent card with image {FileName} and output: {Output}", fileToSend, output);

            if (proc.ExitCode != 0)
            {
                logger.LogError("Send card failed!");

                // try to parse output for next allowed send date
                var regexResult = RegexDateTimeExtractor.DateTimeRegex().Match(output);

                if (regexResult.Success)
                {
                    if (DateTimeOffset.TryParse(regexResult.Value, out var dateTimeOffset))
                    {
                        var currentTimeOffset = DateTimeOffset.Now;
                        var delay = dateTimeOffset - currentTimeOffset;

                        timeToDelayNextTry = (int) delay.TotalMilliseconds;

                        logger.LogInformation("Found next allowed to send date, setting it to {Date}", dateTimeOffset.DateTime);
                    }
                    else
                    {
                        logger.LogWarning("No valid date found in output, waiting 1 hours for next retry");
                        timeToDelayNextTry = (int) TimeSpan.FromHours(1).TotalMilliseconds;
                    }
                }
                else
                {
                    logger.LogError("Unknown error, waiting 1 hours for next retry");
                    timeToDelayNextTry = (int) TimeSpan.FromHours(1).TotalMilliseconds;
                }
            }
            else
            {
                logger.LogInformation("Card was sent successfully");
                File.Delete(Path.Combine(envMediaFolderPath, fileToSend));
            }
        }
        catch (Exception e)
        {
            logger.LogInformation("Send card failed with error: {Error}", e);
            timeToDelayNextTry = (int) TimeSpan.FromMinutes(10).TotalMilliseconds;
        }

        await Task.Delay(timeToDelayNextTry); // In case of error
    } while (true);
}

public static partial class RegexDateTimeExtractor{
    [GeneratedRegex(@"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}[+-]\d{2}:\d{2}")]
    public static partial Regex DateTimeRegex();
}
