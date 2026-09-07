namespace SteamClipRemuxer.Core.Steam;

/// <summary>Protobuf wire types. Only the four that appear in Steam's clip.pb are handled.</summary>
public enum WireType
{
    Varint = 0,
    Fixed64 = 1,
    LengthDelimited = 2,
    Fixed32 = 5,
}

/// <summary>
/// A minimal protobuf wire-format reader.
///
/// Steam publishes no schema for clip.pb, so there is nothing to generate bindings from and
/// no reason to take a dependency on Google.Protobuf to read six fields. The wire format is
/// self-describing enough to walk directly: every field carries its number and wire type, and
/// unknown fields can be skipped without knowing what they mean. That matters here, because
/// the layout is inferred from observed files and Valve may add fields at any time - skipping
/// what we do not recognise is the difference between a forward-compatible reader and one
/// that breaks on the next Steam update.
///
/// Reads never throw on malformed input; they return false so the caller can fall back.
/// </summary>
public ref struct ProtobufReader
{
    private readonly ReadOnlySpan<byte> _data;
    private int _position;

    public ProtobufReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        _position = 0;
    }

    public bool AtEnd => _position >= _data.Length;

    /// <summary>Reads the next field header. False at the end of the buffer or on malformed input.</summary>
    public bool TryReadTag(out int fieldNumber, out WireType wireType)
    {
        fieldNumber = 0;
        wireType = WireType.Varint;

        if (!TryReadVarint(out ulong tag)) return false;

        fieldNumber = (int)(tag >> 3);
        wireType = (WireType)(tag & 0x7);

        // Field 0 is not legal, and a wire type we cannot size is unskippable: either means the
        // buffer is not what we think it is, so stop rather than walk off into noise.
        return fieldNumber > 0 && wireType is
            WireType.Varint or WireType.Fixed64 or WireType.LengthDelimited or WireType.Fixed32;
    }

    public bool TryReadVarint(out ulong value)
    {
        value = 0;
        int shift = 0;

        while (_position < _data.Length)
        {
            byte b = _data[_position++];
            // Ten groups of seven bits is the most a 64-bit value can occupy; beyond that the
            // shift would overflow and silently produce a wrong number.
            if (shift > 63) return false;

            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return true;
            shift += 7;
        }

        return false;
    }

    public bool TryReadLengthDelimited(out ReadOnlySpan<byte> value)
    {
        value = default;
        if (!TryReadVarint(out ulong length)) return false;
        if (length > (ulong)(_data.Length - _position)) return false;

        value = _data.Slice(_position, (int)length);
        _position += (int)length;
        return true;
    }

    public bool TryReadString(out string value)
    {
        value = string.Empty;
        if (!TryReadLengthDelimited(out ReadOnlySpan<byte> bytes)) return false;

        // Steam writes player and map names as UTF-8, including names carrying clan tags and
        // non-Latin scripts. Decoding as anything else mangles them.
        value = System.Text.Encoding.UTF8.GetString(bytes);
        return true;
    }

    /// <summary>Advances past a field whose value we do not need. False if it cannot be sized.</summary>
    public bool TrySkip(WireType wireType)
    {
        switch (wireType)
        {
            case WireType.Varint:
                return TryReadVarint(out _);
            case WireType.LengthDelimited:
                return TryReadLengthDelimited(out _);
            case WireType.Fixed32:
                return Advance(4);
            case WireType.Fixed64:
                return Advance(8);
            default:
                return false;
        }
    }

    private bool Advance(int count)
    {
        if (_data.Length - _position < count) return false;
        _position += count;
        return true;
    }
}
