// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
namespace OrchestratorIDE.Models;

public enum ChatAttachmentKind
{
    Image,
    Text,
    Markdown,
    File,
}

public sealed class ChatAttachment
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public ChatAttachmentKind Kind { get; init; }
    public string FilePath { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string MediaType { get; init; } = "application/octet-stream";
    public long ByteSize { get; init; }

    public bool IsImage => Kind == ChatAttachmentKind.Image;
    public bool IsTextLike => Kind is ChatAttachmentKind.Text or ChatAttachmentKind.Markdown;

    public static ChatAttachment FromPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var info = new FileInfo(fullPath);
        var ext = info.Extension.ToLowerInvariant();

        var kind = ext switch
        {
            ".png" or ".jpg" or ".jpeg" or ".webp" or ".gif" or ".bmp" => ChatAttachmentKind.Image,
            ".md" or ".markdown" => ChatAttachmentKind.Markdown,
            ".txt" or ".json" or ".yml" or ".yaml" or ".xml" or ".csv" or ".ps1" or ".sh"
                or ".cs" or ".ts" or ".tsx" or ".js" or ".jsx" or ".py" or ".go" or ".rs"
                or ".java" or ".cpp" or ".c" or ".h" or ".hpp" or ".sql" => ChatAttachmentKind.Text,
            _ => ChatAttachmentKind.File,
        };

        var mediaType = ext switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            ".md" or ".markdown" or ".txt" => "text/plain",
            ".json" => "application/json",
            ".csv" => "text/csv",
            _ => "application/octet-stream",
        };

        return new ChatAttachment
        {
            Kind = kind,
            FilePath = fullPath,
            DisplayName = info.Name,
            MediaType = mediaType,
            ByteSize = info.Exists ? info.Length : 0,
        };
    }
}
