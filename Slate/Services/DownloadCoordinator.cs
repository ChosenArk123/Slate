using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Web.WebView2.Core;
using Slate.Core;

namespace Slate.Services;

public sealed class DownloadCoordinator
{
    private readonly Dictionary<Guid, CoreWebView2DownloadOperation> _downloadOperations = [];

    public Dictionary<Guid, CoreWebView2DownloadOperation> ActiveOperations => _downloadOperations;
    public int ActiveDownloadCount => _downloadOperations.Count;

    public void RegisterOperation(Guid downloadId, CoreWebView2DownloadOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        _downloadOperations[downloadId] = operation;
    }

    public void UnregisterOperation(Guid downloadId)
    {
        _downloadOperations.Remove(downloadId);
    }

    public CoreWebView2DownloadOperation? GetOperation(Guid downloadId)
    {
        return _downloadOperations.GetValueOrDefault(downloadId);
    }

    public string ResolveSafeDestination(string? configuredDownloadDir, string suggestedFileName)
    {
        string defaultDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        string baseDir = !string.IsNullOrEmpty(configuredDownloadDir) && DownloadSafety.IsSafeLocalDirectory(configuredDownloadDir)
            ? configuredDownloadDir
            : defaultDir;

        if (!Directory.Exists(baseDir))
        {
            try { Directory.CreateDirectory(baseDir); }
            catch { baseDir = defaultDir; }
        }

        string safeName = DownloadSafety.SanitizeFileName(suggestedFileName);
        return DownloadSafety.GetSafeDestinationPath(baseDir, safeName);
    }

    public void CancelOperation(Guid downloadId)
    {
        if (_downloadOperations.TryGetValue(downloadId, out var operation))
        {
            try { operation.Cancel(); } catch { }
            _downloadOperations.Remove(downloadId);
        }
    }

    public void CleanIncompleteFile(string? filePath)
    {
        if (!string.IsNullOrEmpty(filePath))
        {
            DownloadSafety.TryDeleteIncompleteFile(filePath);
        }
    }
}
