using System;
using System.IO;
using System.Text;

namespace Boombox;

// "Artist - Title" from an MP3's ID3v2 tag or an OGG's Vorbis comments, or null when the file has neither. Only the
// start of the file is read, where both live.
internal static class Tags
{
    private const int ReadLimit = 256 * 1024;

    public static string? Title(string file)
    {
        try
        {
            byte[] head = Head(file);
            (string? artist, string? title) = Path.GetExtension(file).Equals(".ogg", StringComparison.OrdinalIgnoreCase)
                ? Vorbis(head)
                : Id3(head);
            if (string.IsNullOrWhiteSpace(title))
            {
                return null;
            }
            return string.IsNullOrWhiteSpace(artist) ? title!.Trim() : $"{artist!.Trim()} - {title!.Trim()}";
        }
        catch (Exception)
        {
            // A broken tag is just no tag.
            return null;
        }
    }

    private static byte[] Head(string file)
    {
        using FileStream stream = File.OpenRead(file);
        byte[] head = new byte[Math.Min(stream.Length, ReadLimit)];
        int read = 0;
        while (read < head.Length)
        {
            int n = stream.Read(head, read, head.Length - read);
            if (n == 0)
            {
                break;
            }
            read += n;
        }
        return head;
    }

    private static (string?, string?) Id3(byte[] b)
    {
        if (b.Length < 10 || b[0] != 'I' || b[1] != 'D' || b[2] != '3')
        {
            return (null, null);
        }
        int version = b[3];
        int end = Math.Min(b.Length, 10 + Syncsafe(b, 6));
        int idLength = version == 2 ? 3 : 4;
        int headerLength = version == 2 ? 6 : 10;
        string? artist = null, title = null;
        for (int at = 10; at + headerLength <= end;)
        {
            string id = Encoding.ASCII.GetString(b, at, idLength);
            if (id[0] == '\0')
            {
                break;
            }
            int size = version switch
            {
                2 => (b[at + 3] << 16) | (b[at + 4] << 8) | b[at + 5],
                4 => Syncsafe(b, at + 4),
                _ => (b[at + 4] << 24) | (b[at + 5] << 16) | (b[at + 6] << 8) | b[at + 7],
            };
            int body = at + headerLength;
            if (size <= 0 || body + size > end)
            {
                break;
            }
            if (id is "TIT2" or "TT2")
            {
                title = Text(b, body, size);
            }
            else if (id is "TPE1" or "TP1")
            {
                artist = Text(b, body, size);
            }
            at = body + size;
        }
        return (artist, title);
    }

    private static int Syncsafe(byte[] b, int at) => (b[at] << 21) | (b[at + 1] << 14) | (b[at + 2] << 7) | b[at + 3];

    // A text frame starts with its encoding: 0 Latin-1, 1 UTF-16 with a BOM, 2 UTF-16BE, 3 UTF-8.
    private static string Text(byte[] b, int at, int size)
    {
        Encoding encoding = b[at] switch
        {
            1 => Encoding.Unicode,
            2 => Encoding.BigEndianUnicode,
            3 => Encoding.UTF8,
            _ => Encoding.GetEncoding("ISO-8859-1"),
        };
        int start = at + 1, length = size - 1;
        if (b[at] == 1 && length >= 2)
        {
            encoding = b[start] == 0xFE ? Encoding.BigEndianUnicode : Encoding.Unicode;
            start += 2;
            length -= 2;
        }
        return encoding.GetString(b, start, length).TrimEnd('\0');
    }

    private static (string?, string?) Vorbis(byte[] b)
    {
        byte[] marker = [3, (byte)'v', (byte)'o', (byte)'r', (byte)'b', (byte)'i', (byte)'s'];
        int at = IndexOf(b, marker);
        if (at < 0)
        {
            return (null, null);
        }
        at += marker.Length;
        at += 4 + BitConverter.ToInt32(b, at);
        int count = BitConverter.ToInt32(b, at);
        at += 4;
        string? artist = null, title = null;
        for (int i = 0; i < count && at + 4 <= b.Length; i++)
        {
            int length = BitConverter.ToInt32(b, at);
            at += 4;
            if (length < 0 || at + length > b.Length)
            {
                break;
            }
            string comment = Encoding.UTF8.GetString(b, at, length);
            at += length;
            int eq = comment.IndexOf('=');
            if (eq > 0)
            {
                string key = comment[..eq].ToUpperInvariant();
                if (key == "TITLE")
                {
                    title = comment[(eq + 1)..];
                }
                else if (key == "ARTIST")
                {
                    artist = comment[(eq + 1)..];
                }
            }
        }
        return (artist, title);
    }

    private static int IndexOf(byte[] b, byte[] marker)
    {
        for (int i = 0; i + marker.Length <= b.Length; i++)
        {
            int j = 0;
            while (j < marker.Length && b[i + j] == marker[j])
            {
                j++;
            }
            if (j == marker.Length)
            {
                return i;
            }
        }
        return -1;
    }
}
