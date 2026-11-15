namespace WinCAN.Core.Formatting;

/// <summary>Display helpers shared by the UI (payloads are shown as space-delimited bytes).</summary>
public static class HexFormat
{
    /// <summary>Formats bytes as uppercase, space-delimited hex: <c>"FE 36 12"</c>.</summary>
    public static string SpaceDelimited(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
        {
            return string.Empty;
        }

        return string.Create(data.Length * 3 - 1, data.ToArray(), static (span, bytes) =>
        {
            const string digits = "0123456789ABCDEF";
            var position = 0;
            for (var i = 0; i < bytes.Length; i++)
            {
                if (i > 0)
                {
                    span[position++] = ' ';
                }

                span[position++] = digits[bytes[i] >> 4];
                span[position++] = digits[bytes[i] & 0x0F];
            }
        });
    }
}
