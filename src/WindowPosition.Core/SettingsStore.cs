using System.Buffers.Binary;
using System.Text;

namespace WindowPosition.Core;

/// <summary>Versioned compact configuration, saved atomically beside the executable.</summary>
public sealed class SettingsStore
{
    private const int MaximumFileSize = 4 * 1024 * 1024;
    private const int MaximumRuleCount = 10_000;
    private const int MaximumTitleBytes = 128 * 1024;
    private const byte FormatVersion = 1;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly string _path;
    private bool _preserveSourceOnSave;

    public SettingsStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
    }

    public string? LoadWarning { get; private set; }

    public AppSettings Load()
    {
        LoadWarning = null;
        _preserveSourceOnSave = false;
        if (!File.Exists(_path) && !Directory.Exists(_path))
            return new();
        try
        {
            return ReadAndValidate(_path);
        }
        catch (Exception error) when (IsRecoverable(error))
        {
            _preserveSourceOnSave = File.Exists(_path);
            LoadWarning = $"設定ファイルを読み込めませんでした: {error.Message} 空の設定で起動します。元の設定ファイルは保全されます。";
            return new();
        }
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Validate(settings);
        var data = Encode(settings);
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var file = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(data);
                file.Flush(flushToDisk: true);
            }
            if (File.Exists(_path))
            {
                // Only unreadable source data needs a permanent backup; normal saves keep one cfg.
                var backupPath = _preserveSourceOnSave
                    ? $"{_path}.corrupt.{DateTime.UtcNow:yyyyMMdd-HHmmss}.{Guid.NewGuid():N}"
                    : null;
                File.Replace(temporaryPath, _path, backupPath, ignoreMetadataErrors: false);
            }
            else
            {
                File.Move(temporaryPath, _path);
            }
            _preserveSourceOnSave = false;
            LoadWarning = null;
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static byte[] Encode(AppSettings settings)
    {
        using var payload = new MemoryStream();
        payload.WriteByte((byte)'W');
        payload.WriteByte((byte)'P');
        payload.WriteByte(FormatVersion);
        WriteUnsigned(payload, (uint)settings.Rules.Count);
        foreach (var rule in settings.Rules)
        {
            payload.Write(rule.Id.ToByteArray());
            payload.WriteByte((byte)((rule.Enabled ? 1 : 0) | ((int)rule.Mode << 1) | ((int)rule.MatchMode << 2)));
            WriteUnsigned(payload, ZigZag(rule.X));
            WriteUnsigned(payload, ZigZag(rule.Y));
            WriteUnsigned(payload, (uint)rule.Width);
            WriteUnsigned(payload, (uint)rule.Height);
            var title = StrictUtf8.GetBytes(rule.Title);
            WriteUnsigned(payload, (uint)title.Length);
            payload.Write(title);
        }
        var checksum = Checksum(payload.GetBuffer().AsSpan(0, (int)payload.Length));
        Span<byte> checksumBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(checksumBytes, checksum);
        payload.Write(checksumBytes);
        return payload.ToArray();
    }

    private static AppSettings ReadAndValidate(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length < 8 || file.Length > MaximumFileSize)
            throw new InvalidDataException("設定ファイルのサイズが不正です");
        var data = new byte[(int)file.Length];
        file.ReadExactly(data);
        if (data[0] != (byte)'W' || data[1] != (byte)'P')
            throw new InvalidDataException("設定ファイルの形式が不正です");
        if (data[2] != FormatVersion)
            throw new InvalidDataException($"未対応の設定バージョンです ({data[2]})");
        var payloadLength = data.Length - 4;
        if (Checksum(data.AsSpan(0, payloadLength)) != BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(payloadLength)))
            throw new InvalidDataException("設定ファイルが破損しています（チェックサム不一致）");

        using var payload = new MemoryStream(data, 0, payloadLength, writable: false);
        payload.Position = 3;
        var count = ReadUnsigned(payload);
        // Every rule needs at least 23 bytes; reject impossible lengths before allocating lists/titles.
        if (count > MaximumRuleCount || count > (payload.Length - payload.Position) / 23)
            throw new InvalidDataException("設定の項目数が不正です");
        var rules = new List<WindowRule>((int)count);
        Span<byte> id = stackalloc byte[16];
        for (var index = 0; index < count; index++)
        {
            payload.ReadExactly(id);
            var flags = payload.ReadByte();
            if (flags < 0 || (flags & ~7) != 0)
                throw new InvalidDataException("設定のフラグが不正です");
            var x = UnZigZag(ReadUnsigned(payload));
            var y = UnZigZag(ReadUnsigned(payload));
            var width = ReadUnsigned(payload);
            var height = ReadUnsigned(payload);
            var titleLength = ReadUnsigned(payload);
            if (width is 0 or > int.MaxValue || height is 0 or > int.MaxValue)
                throw new InvalidDataException("設定のサイズが不正です");
            if (titleLength is 0 or > MaximumTitleBytes || titleLength > payload.Length - payload.Position)
                throw new InvalidDataException("設定のタイトル長が不正です");
            var title = new byte[(int)titleLength];
            payload.ReadExactly(title);
            rules.Add(new WindowRule
            {
                Id = new Guid(id),
                Enabled = (flags & 1) != 0,
                Mode = (FollowMode)((flags >> 1) & 1),
                MatchMode = (TitleMatchMode)((flags >> 2) & 1),
                X = x,
                Y = y,
                Width = (int)width,
                Height = (int)height,
                Title = StrictUtf8.GetString(title)
            });
        }
        if (payload.Position != payload.Length)
            throw new InvalidDataException("設定の末尾に不正なデータがあります");
        var settings = new AppSettings { Rules = rules };
        Validate(settings);
        return settings;
    }

    private static void Validate(AppSettings settings)
    {
        if (settings.Rules is null || settings.Rules.Count > MaximumRuleCount)
            throw new InvalidDataException("項目一覧または項目数が不正です");
        var ids = new HashSet<Guid>();
        long totalBytes = 8;
        foreach (var rule in settings.Rules)
        {
            if (rule is null)
                throw new InvalidDataException("不正な項目があります");
            if (RuleValidation.GetError(rule) is { } error)
                throw new InvalidDataException(error);
            if (!ids.Add(rule.Id))
                throw new InvalidDataException("項目の ID が重複しています");
            var titleBytes = StrictUtf8.GetByteCount(rule.Title);
            if (titleBytes > MaximumTitleBytes)
                throw new InvalidDataException("ウィンドウタイトルが長すぎます");
            totalBytes += 17 + UnsignedLength(ZigZag(rule.X)) + UnsignedLength(ZigZag(rule.Y))
                + UnsignedLength((uint)rule.Width) + UnsignedLength((uint)rule.Height)
                + UnsignedLength((uint)titleBytes) + titleBytes;
        }
        totalBytes += UnsignedLength((uint)settings.Rules.Count) - 1;
        if (totalBytes > MaximumFileSize)
            throw new InvalidDataException("設定ファイルが大きすぎます（上限 4 MiB）");
    }

    private static void WriteUnsigned(Stream output, uint value)
    {
        while (value >= 128)
        {
            output.WriteByte((byte)(value | 128));
            value >>= 7;
        }
        output.WriteByte((byte)value);
    }

    private static uint ReadUnsigned(Stream input)
    {
        uint result = 0;
        for (var shift = 0; shift <= 28; shift += 7)
        {
            var next = input.ReadByte();
            if (next < 0)
                throw new EndOfStreamException("設定ファイルが途中で切れています");
            if (shift == 28 && next > 15)
                throw new InvalidDataException("設定の整数値が不正です");
            result |= (uint)(next & 127) << shift;
            if ((next & 128) == 0)
            {
                if (shift > 0 && next == 0)
                    throw new InvalidDataException("設定の整数表現が不正です");
                return result;
            }
        }
        throw new InvalidDataException("設定の整数値が不正です");
    }

    private static int UnsignedLength(uint value)
    {
        var length = 1;
        while (value >= 128) { length++; value >>= 7; }
        return length;
    }

    private static uint ZigZag(int value) => unchecked((uint)((value << 1) ^ (value >> 31)));
    private static int UnZigZag(uint value) => unchecked((int)(value >> 1) ^ -((int)value & 1));

    private static uint Checksum(ReadOnlySpan<byte> bytes)
    {
        var result = uint.MaxValue;
        foreach (var value in bytes)
        {
            result ^= value;
            for (var bit = 0; bit < 8; bit++)
                result = (result >> 1) ^ (0xEDB88320u & unchecked((uint)-(int)(result & 1)));
        }
        return ~result;
    }

    private static bool IsRecoverable(Exception error) =>
        error is IOException or InvalidDataException or UnauthorizedAccessException or DecoderFallbackException or NotSupportedException;
}
