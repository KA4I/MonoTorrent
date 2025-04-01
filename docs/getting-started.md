# Getting Started with MonoTorrent

This guide provides a basic example of how to use the MonoTorrent library to download a torrent file.

## Prerequisites

*   .NET SDK installed.
*   MonoTorrent library added to your project (e.g., via NuGet).

## Basic Download Example

Here's a simple console application demonstrating how to download a torrent:

```csharp
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MonoTorrent;
using MonoTorrent.Client;

public class Program
{
    public static async Task Main(string[] args)
    {
        // --- Configuration ---
        string torrentPath = "path/to/your/torrentfile.torrent"; // Replace with the actual path
        string downloadDirectory = "downloads"; // Directory to save downloaded files

        // Ensure download directory exists
        Directory.CreateDirectory(downloadDirectory);

        // --- Engine Setup ---
        // Create default engine settings. See EngineSettings for more options.
        var settings = new EngineSettingsBuilder {
             AllowPortForwarding = true,
             AutoSaveLoadFastResume = true,
             CacheDirectory = "cache" // Recommended to keep cache separate
        }.ToSettings();

        // Create the ClientEngine
        using var engine = new ClientEngine(settings);

        // --- Load and Add Torrent ---
        Torrent? torrent = null;
        try
        {
            torrent = await Torrent.LoadAsync(torrentPath);
        }
        catch (Exception e)
        {
            Console.WriteLine($"Couldn't load torrent file: {e.Message}");
            return;
        }

        // Add the torrent to the engine
        var manager = await engine.AddAsync(torrent, downloadDirectory);

        // --- Start Download ---
        await manager.StartAsync();
        Console.WriteLine($"Starting download for: {manager.Torrent.Name}");

        // --- Monitor Progress (Simple Example) ---
        // Keep the application running while downloading/seeding
        var cancellationTokenSource = new CancellationTokenSource();
        Console.CancelKeyPress += (sender, e) => {
            e.Cancel = true; // Prevent default Ctrl+C behavior
            cancellationTokenSource.Cancel();
        };

        while (engine.IsRunning && !cancellationTokenSource.IsCancellationRequested)
        {
            if (manager.State == TorrentState.Stopped || manager.State == TorrentState.Error)
                break; // Exit if torrent stops or errors

            Console.Write($"\rProgress: {manager.Progress:0.00}% - Download Speed: {engine.TotalDownloadRate / 1024.0:0.0} kB/s - Upload Speed: {engine.TotalUploadRate / 1024.0:0.0} kB/s - Peers: {manager.OpenConnections}");
            await Task.Delay(1000, cancellationTokenSource.Token); // Update every second
        }
        Console.WriteLine(); // New line after loop finishes

        // --- Stop and Cleanup ---
        Console.WriteLine("Stopping torrent...");
        await manager.StopAsync();
        Console.WriteLine("Stopping engine...");
        // Optionally save engine state here if needed: await engine.SaveStateAsync("engine.state");
        Console.WriteLine("Engine stopped.");
    }
}
```

**Explanation:**

1.  **Configuration:** Define the path to your `.torrent` file and the directory where you want to save the downloaded files.
2.  **Engine Setup:**
    *   Create `EngineSettings` to configure the client (port forwarding, fast resume, cache location are common options).
    *   Instantiate the `ClientEngine` with the settings. The `using` statement ensures it's disposed properly.
3.  **Load and Add Torrent:**
    *   Load the `.torrent` file using `Torrent.LoadAsync`.
    *   Add the loaded `Torrent` object to the `ClientEngine` using `engine.AddAsync`, specifying the download directory. This returns a `TorrentManager`.
4.  **Start Download:** Call `manager.StartAsync()` on the `TorrentManager` to begin the download process (hashing, connecting to peers, etc.).
5.  **Monitor Progress:** The example includes a simple loop that prints progress, download/upload speed, and peer count every second. It also handles Ctrl+C for graceful shutdown. In a real application, you would likely use events like `TorrentManager.PieceHashed` and `TorrentManager.TorrentStateChanged` for more robust progress tracking and state management.
6.  **Stop and Cleanup:** When finished (or interrupted), call `manager.StopAsync()` to gracefully stop the torrent (announcing to trackers) and then the `ClientEngine` will implicitly stop if no other torrents are running (or you can explicitly dispose it).

This provides a basic framework. You can explore the `EngineSettings` and `TorrentSettings` classes for more advanced configuration options.