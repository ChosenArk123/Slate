using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using Slate.Core;
using Slate.Engine;
using Slate.Services;

namespace Slate;

public sealed record ProcessMemorySnapshot(
    int ProcessId,
    string Kind,
    long WorkingSetBytes,
    long PrivateBytes)
{
    public double WorkingSetMB => Math.Round(WorkingSetBytes / (1024.0 * 1024.0), 2);
    public double PrivateBytesMB => Math.Round(PrivateBytes / (1024.0 * 1024.0), 2);
}

public sealed record ProcessTreeSnapshot(
    int TotalProcesses,
    long TotalWorkingSetBytes,
    long TotalPrivateBytes,
    long MainProcessWorkingSet,
    long MainProcessPrivateBytes,
    List<ProcessMemorySnapshot> Children)
{
    public double TotalWorkingSetMB => Math.Round(TotalWorkingSetBytes / (1024.0 * 1024.0), 2);
    public double TotalPrivateBytesMB => Math.Round(TotalPrivateBytes / (1024.0 * 1024.0), 2);
}

public static class MemoryBenchmarkRunner
{
    public static async Task RunBenchmarkAsync(MainWindow window, string outputPath)
    {
        HttpListener? listener = null;
        try
        {
            // 1. Start ephemeral loopback server on 127.0.0.1:0
            var tcpListener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            tcpListener.Start();
            int port = ((IPEndPoint)tcpListener.LocalEndpoint).Port;
            tcpListener.Stop();

            listener = new HttpListener();
            string baseUri = $"http://127.0.0.1:{port}/";
            listener.Prefixes.Add(baseUri);
            listener.Start();

            // Background HTTP responder
            _ = Task.Run(async () =>
            {
                while (listener.IsListening)
                {
                    try
                    {
                        var context = await listener.GetContextAsync();
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                string path = context.Request.Url?.AbsolutePath ?? "/";
                                byte[] responseBytes;
                                string contentType = "text/html; charset=utf-8";

                                if (path.Equals("/static", StringComparison.OrdinalIgnoreCase))
                                {
                                    string html = "<!DOCTYPE html><html><head><title>Static Tab</title></head><body><h1>Static Content</h1><p>Simple lightweight page.</p></body></html>";
                                    responseBytes = Encoding.UTF8.GetBytes(html);
                                }
                                else if (path.Equals("/heavy-js", StringComparison.OrdinalIgnoreCase))
                                {
                                    string html = """
                                        <!DOCTYPE html>
                                        <html>
                                        <head><title>Heavy JS Tab</title></head>
                                        <body>
                                          <h1>Heavy JS Benchmark Page</h1>
                                          <div id="status">Allocating...</div>
                                          <script>
                                            const store = [];
                                            for (let i = 0; i < 400000; i++) {
                                              store.push({ id: i, payload: "item_" + i, buf: new Uint8Array(32) });
                                            }
                                            window._benchmarkData = store;
                                            const body = document.body;
                                            for (let i = 0; i < 500; i++) {
                                              const div = document.createElement("div");
                                              div.textContent = "DOM Node #" + i + " " + Math.random();
                                              body.appendChild(div);
                                            }
                                            document.getElementById("status").textContent = "Allocated " + store.length + " objects.";
                                          </script>
                                        </body>
                                        </html>
                                        """;
                                    responseBytes = Encoding.UTF8.GetBytes(html);
                                }
                                else if (path.Equals("/audio", StringComparison.OrdinalIgnoreCase))
                                {
                                    string html = """
                                        <!DOCTYPE html>
                                        <html>
                                        <head><title>Audio Playback Tab</title></head>
                                        <body>
                                          <h1>Audio Stream Active</h1>
                                          <button id="playBtn" onclick="playAudio()">Play Audio</button>
                                          <script>
                                            window.playAudio = function() {
                                              try {
                                                const ctx = window._audioCtx || new (window.AudioContext || window.webkitAudioContext)();
                                                if (ctx.state === 'suspended') {
                                                  ctx.resume();
                                                }
                                                const osc = ctx.createOscillator();
                                                const gain = ctx.createGain();
                                                osc.type = 'sine';
                                                osc.frequency.value = 440;
                                                gain.gain.value = 0.05; // -26 dBFS (well above Chromium silence threshold)
                                                osc.connect(gain);
                                                gain.connect(ctx.destination);
                                                osc.start();
                                                window._audioCtx = ctx;
                                                return true;
                                              } catch (e) { return false; }
                                            };
                                            window.addEventListener('DOMContentLoaded', () => {
                                              window.playAudio();
                                            });
                                          </script>
                                        </body>
                                        </html>
                                        """;
                                    responseBytes = Encoding.UTF8.GetBytes(html);
                                }
                                else if (path.Equals("/large-doc", StringComparison.OrdinalIgnoreCase))
                                {
                                    var sb = new StringBuilder();
                                    sb.Append("<!DOCTYPE html><html><head><title>Large Document Tab</title></head><body><h1>Large Document</h1>");
                                    for (int i = 0; i < 2000; i++)
                                    {
                                        sb.Append($"<p>Paragraph #{i}: Slate memory reclamation benchmark verifies physical renderer process deallocation under gaming pressure.</p>");
                                    }
                                    sb.Append("</body></html>");
                                    responseBytes = Encoding.UTF8.GetBytes(sb.ToString());
                                }
                                else
                                {
                                    responseBytes = Encoding.UTF8.GetBytes("<!DOCTYPE html><html><body>Slate Benchmark</body></html>");
                                }

                                context.Response.ContentType = contentType;
                                context.Response.ContentLength64 = responseBytes.Length;
                                await context.Response.OutputStream.WriteAsync(responseBytes);
                                context.Response.OutputStream.Close();
                            }
                            catch { }
                        });
                    }
                    catch { break; }
                }
            });

            // 2. Open 12 representative tabs
            string[] routes =
            [
                "static",
                "heavy-js",
                "audio",
                "large-doc",
                "heavy-js",
                "static",
                "large-doc",
                "heavy-js",
                "static",
                "heavy-js",
                "large-doc",
                "static"
            ];

            var tabIds = new List<Guid>();
            for (int i = 0; i < routes.Length; i++)
            {
                string url = baseUri + routes[i];
                if (i == 0)
                {
                    window.Session.ActiveTab.Url = url;
                    await window.BenchmarkActivateTabAsync(window.Session.ActiveTab.Id);
                    tabIds.Add(window.Session.ActiveTab.Id);
                }
                else
                {
                    await window.BenchmarkNewTabAsync(url);
                    tabIds.Add(window.Session.ActiveTab.Id);
                }

                if (routes[i] == "audio")
                {
                    var runtime = window.TabManager.GetRuntime(window.Session.ActiveTab.Id);
                    if (runtime?.Initialization is not null) await runtime.Initialization;
                    await Task.Delay(600);
                    if (runtime?.View?.CoreWebView2 is not null)
                    {
                        try
                        {
                            await runtime.View.CoreWebView2.CallDevToolsProtocolMethodAsync("Runtime.evaluate",
                                "{\"expression\":\"playAudio()\",\"userGesture\":true}");
                            await runtime.View.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent",
                                "{\"type\":\"mousePressed\",\"x\":20,\"y\":20,\"button\":\"left\",\"clickCount\":1}");
                            await runtime.View.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent",
                                "{\"type\":\"mouseReleased\",\"x\":20,\"y\":20,\"button\":\"left\",\"clickCount\":1}");
                        }
                        catch { }
                    }
                }

                await Task.Delay(250);
            }

            // Ensure last tab is active/focused
            await window.BenchmarkActivateTabAsync(tabIds[^1]);

            // Allow all renderers and JS execution to settle
            await Task.Delay(4000);

            // 3. Capture baseline process-tree snapshot
            var baseline = CaptureSnapshot(window.EngineService);

            // Record whether audio is actively playing before discard
            bool hadAudioPlayingBeforeDiscard = window.TabManager.AllRuntimes.Any(r => r.View.CoreWebView2?.IsDocumentPlayingAudio == true);

            // 4. Trigger gaming discard with zero inactive duration for benchmark repeatability
            var discardedIds = window.BenchmarkDiscardTabs(TimeSpan.Zero, maxRetained: 1);

            // Wait for Windows and WebView2 to terminate discarded renderers and release working set
            await Task.Delay(4000);

            // 5. Capture post-discard snapshot
            var postDiscard = CaptureSnapshot(window.EngineService);

            // Verify audio tab was preserved if active
            bool audioTabPreserved = window.TabManager.AllRuntimes.Any(r => r.View.CoreWebView2?.IsDocumentPlayingAudio == true);
            bool audioCriteriaMet = hadAudioPlayingBeforeDiscard ? audioTabPreserved : true;

            // 6. Reactivate 2 discarded tabs to prove reliable recreation and memory re-allocation
            var tabsToRestore = window.Session.State.Tabs
                .Where(t => t.IsSleeping && discardedIds.Contains(t.Id))
                .Take(2)
                .ToList();

            bool restorationSucceeded = true;
            foreach (var tab in tabsToRestore)
            {
                try
                {
                    await window.BenchmarkActivateTabAsync(tab.Id);
                    await Task.Delay(1500);
                    if (tab.IsSleeping || !window.TabManager.HasRuntime(tab.Id))
                    {
                        restorationSucceeded = false;
                    }
                }
                catch
                {
                    restorationSucceeded = false;
                }
            }

            await Task.Delay(2000);
            var postRestore = CaptureSnapshot(window.EngineService);

            // 7. Compute savings
            double wsSavedMB = Math.Round((baseline.TotalWorkingSetBytes - postDiscard.TotalWorkingSetBytes) / (1024.0 * 1024.0), 2);
            double wsReductionPct = baseline.TotalWorkingSetBytes > 0
                ? Math.Round((1.0 - (double)postDiscard.TotalWorkingSetBytes / baseline.TotalWorkingSetBytes) * 100.0, 1)
                : 0;

            double pbSavedMB = Math.Round((baseline.TotalPrivateBytes - postDiscard.TotalPrivateBytes) / (1024.0 * 1024.0), 2);
            double pbReductionPct = baseline.TotalPrivateBytes > 0
                ? Math.Round((1.0 - (double)postDiscard.TotalPrivateBytes / baseline.TotalPrivateBytes) * 100.0, 1)
                : 0;

            int terminatedProcesses = Math.Max(0, baseline.TotalProcesses - postDiscard.TotalProcesses);

            var report = new
            {
                passed = wsReductionPct >= 20.0 && restorationSucceeded && audioCriteriaMet,
                tabsTotal = routes.Length,
                tabsDiscarded = discardedIds.Count,
                audioTabPreserved,
                restorationSucceeded,
                baseline = new
                {
                    totalWorkingSetMB = baseline.TotalWorkingSetMB,
                    totalPrivateBytesMB = baseline.TotalPrivateBytesMB,
                    totalProcesses = baseline.TotalProcesses,
                    mainProcessWorkingSetMB = Math.Round(baseline.MainProcessWorkingSet / (1024.0 * 1024.0), 2),
                    childProcesses = baseline.Children
                },
                postDiscard = new
                {
                    totalWorkingSetMB = postDiscard.TotalWorkingSetMB,
                    totalPrivateBytesMB = postDiscard.TotalPrivateBytesMB,
                    totalProcesses = postDiscard.TotalProcesses,
                    mainProcessWorkingSetMB = Math.Round(postDiscard.MainProcessWorkingSet / (1024.0 * 1024.0), 2),
                    childProcesses = postDiscard.Children
                },
                savings = new
                {
                    workingSetSavedMB = wsSavedMB,
                    workingSetReductionPercent = wsReductionPct,
                    privateBytesSavedMB = pbSavedMB,
                    privateBytesReductionPercent = pbReductionPct,
                    rendererProcessesTerminated = terminatedProcesses
                },
                postRestore = new
                {
                    totalWorkingSetMB = postRestore.TotalWorkingSetMB,
                    totalPrivateBytesMB = postRestore.TotalPrivateBytesMB,
                    totalProcesses = postRestore.TotalProcesses
                }
            };

            string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
            File.WriteAllText(outputPath, json);

            Console.WriteLine("===============================================================================");
            Console.WriteLine("                SLATE GAMING EFFICIENCY MEMORY BENCHMARK");
            Console.WriteLine("===============================================================================");
            Console.WriteLine($"Tabs Opened:                 {routes.Length}");
            Console.WriteLine($"Tabs Discarded:              {discardedIds.Count}");
            Console.WriteLine($"Audio Playback Preserved:    {audioTabPreserved}");
            Console.WriteLine($"Restoration Succeeded:       {restorationSucceeded}");
            Console.WriteLine("-------------------------------------------------------------------------------");
            Console.WriteLine($"Baseline Working Set:        {baseline.TotalWorkingSetMB:F1} MB ({baseline.TotalProcesses} processes)");
            Console.WriteLine($"Post-Discard Working Set:    {postDiscard.TotalWorkingSetMB:F1} MB ({postDiscard.TotalProcesses} processes)");
            Console.WriteLine($"Physical RAM Reclaimed:      {wsSavedMB:F1} MB ({wsReductionPct:F1}% reduction)");
            Console.WriteLine($"Private Commit Reclaimed:    {pbSavedMB:F1} MB ({pbReductionPct:F1}% reduction)");
            Console.WriteLine($"Post-Restore Working Set:    {postRestore.TotalWorkingSetMB:F1} MB ({postRestore.TotalProcesses} processes)");
            Console.WriteLine("===============================================================================");
        }
        catch (Exception ex)
        {
            var err = new { passed = false, error = ex.ToString() };
            try { File.WriteAllText(outputPath, JsonSerializer.Serialize(err)); } catch { }
            Console.Error.WriteLine("Memory benchmark failed: " + ex);
        }
        finally
        {
            try { listener?.Stop(); } catch { }
            Environment.Exit(0);
        }
    }

    private static ProcessTreeSnapshot CaptureSnapshot(BrowserEngineService engineService)
    {
        var current = Process.GetCurrentProcess();
        long totalWs = current.WorkingSet64;
        long totalPb = current.PrivateMemorySize64;
        var children = new List<ProcessMemorySnapshot>();

        try
        {
            var childInfos = engineService.GetProcessInfos();
            if (childInfos is not null)
            {
                foreach (var info in childInfos)
                {
                    if (info.ProcessId == current.Id) continue;
                    try
                    {
                        using var child = Process.GetProcessById(info.ProcessId);
                        long ws = child.WorkingSet64;
                        long pb = child.PrivateMemorySize64;
                        totalWs += ws;
                        totalPb += pb;
                        children.Add(new ProcessMemorySnapshot(info.ProcessId, info.Kind.ToString(), ws, pb));
                    }
                    catch { }
                }
            }
        }
        catch { }

        return new ProcessTreeSnapshot(
            1 + children.Count,
            totalWs,
            totalPb,
            current.WorkingSet64,
            current.PrivateMemorySize64,
            children);
    }
}
