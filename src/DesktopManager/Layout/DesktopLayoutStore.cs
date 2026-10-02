using System.IO;
using System.Text.Json;

namespace DesktopManager.Layout;

/// <summary>
/// Reads and writes the user-visible desktop region layout.
/// </summary>
public sealed class DesktopLayoutStore
{
    private const long MaximumFileSize = 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly string _filePath;

    public DesktopLayoutStore(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("桌面布局路径不能为空。", nameof(filePath));
        }

        _filePath = Path.GetFullPath(filePath);
    }

    public static string DefaultFilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DesktopManager",
        "desktop-layout.json");

    public DesktopLayoutDocument Load()
    {
        if (!File.Exists(_filePath))
        {
            return DesktopLayoutDocument.Empty;
        }

        using FileStream stream = new(
            _filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);

        if (stream.Length > MaximumFileSize)
        {
            throw new InvalidDataException("桌面布局文件超过 1 MiB。", null);
        }

        DesktopLayoutDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<DesktopLayoutDocument>(stream, JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("桌面布局文件不是有效的 JSON。", exception);
        }
        catch (NotSupportedException exception)
        {
            throw new InvalidDataException("桌面布局文件包含不支持的数据。", exception);
        }

        Validate(document);
        return document!;
    }

    public void Save(DesktopLayoutDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        Validate(document);

        string? directory = Path.GetDirectoryName(_filePath);
        if (directory is null)
        {
            throw new InvalidOperationException("桌面布局路径必须包含目录。");
        }

        Directory.CreateDirectory(directory);
        string temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(_filePath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (FileStream stream = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                options: FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, document, JsonOptions);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(_filePath))
            {
                File.Replace(temporaryPath, _filePath, destinationBackupFileName: null);
            }
            else
            {
                File.Move(temporaryPath, _filePath);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static void Validate(DesktopLayoutDocument? document)
    {
        if (document is null)
        {
            throw new InvalidDataException("桌面布局文档不能为空。");
        }

        if (document.Version != DesktopLayoutDocument.CurrentVersion)
        {
            throw new InvalidDataException("桌面布局版本不受支持。");
        }

        if (document.Regions is null)
        {
            throw new InvalidDataException("桌面布局分区列表不能为空。");
        }

        if (document.Regions.Length > DesktopLayoutDocument.MaxRegionCount)
        {
            throw new InvalidDataException(
                $"桌面布局最多包含 {DesktopLayoutDocument.MaxRegionCount} 个分区。");
        }

        HashSet<Guid> regionIds = [];
        HashSet<string> memberIdentities = new(StringComparer.OrdinalIgnoreCase);

        foreach (DesktopRegionLayout? region in document.Regions)
        {
            if (region is null)
            {
                throw new InvalidDataException("桌面布局不能包含空分区。");
            }

            if (region.Id == Guid.Empty || !regionIds.Add(region.Id))
            {
                throw new InvalidDataException("桌面布局包含空或重复的分区 ID。");
            }

            if (string.IsNullOrWhiteSpace(region.Name))
            {
                throw new InvalidDataException("桌面布局分区名称不能为空。");
            }

            if (!IsRgbColor(region.Color))
            {
                throw new InvalidDataException("桌面布局分区颜色必须是 #RRGGBB。");
            }

            if (double.IsNaN(region.BackgroundOpacity) ||
                double.IsInfinity(region.BackgroundOpacity) ||
                region.BackgroundOpacity < DesktopRegionLayout.MinBackgroundOpacity ||
                region.BackgroundOpacity > DesktopRegionLayout.MaxBackgroundOpacity)
            {
                throw new InvalidDataException("桌面布局分区透明度超出允许范围。");
            }

            if (region.Width <= 0 || region.Height <= 0)
            {
                throw new InvalidDataException("桌面布局分区宽高必须为正数。");
            }

            if (region.MemberIdentities is null)
            {
                throw new InvalidDataException("桌面布局成员列表不能为空。");
            }

            foreach (string? identity in region.MemberIdentities)
            {
                if (string.IsNullOrWhiteSpace(identity) || !memberIdentities.Add(identity))
                {
                    throw new InvalidDataException("桌面布局包含空或重复的成员身份。");
                }
            }
        }
    }

    private static bool IsRgbColor(string? color)
    {
        if (color is null || color.Length != 7 || color[0] != '#')
        {
            return false;
        }

        for (int index = 1; index < color.Length; index++)
        {
            char character = color[index];
            bool isHexDigit = character is >= '0' and <= '9' or
                >= 'A' and <= 'F' or
                >= 'a' and <= 'f';
            if (!isHexDigit)
            {
                return false;
            }
        }

        return true;
    }
}
