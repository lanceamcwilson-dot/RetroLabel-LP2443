#if HAS_ZXING
using ZXing;
using ZXing.Common;

namespace LabelPrinter.Shared;

/// <summary>ZXing.Net implementation of the barcode-check decoder hook.</summary>
public static class ZxingDecoder
{
    static readonly List<BarcodeFormat> Formats = new()
    {
        BarcodeFormat.CODE_128, BarcodeFormat.CODE_39, BarcodeFormat.CODE_93, BarcodeFormat.ITF, BarcodeFormat.EAN_13, BarcodeFormat.UPC_A,
        BarcodeFormat.QR_CODE, BarcodeFormat.DATA_MATRIX, BarcodeFormat.PDF_417, BarcodeFormat.AZTEC, BarcodeFormat.MAXICODE,
    };

    public static List<string> Decode(byte[] gray, int w, int h)
    {
        var reader = new BarcodeReaderGeneric
        {
            AutoRotate = true,
            Options = new DecodingOptions { TryHarder = true, PossibleFormats = Formats },
        };
        var src = new RGBLuminanceSource(gray, w, h, RGBLuminanceSource.BitmapFormat.Gray8);
        var results = reader.DecodeMultiple(src);
        var list = new List<string>();
        if (results != null) foreach (var r in results) if (!string.IsNullOrEmpty(r.Text)) list.Add($"{r.BarcodeFormat}:{r.Text}");
        return list;
    }
}
#endif
