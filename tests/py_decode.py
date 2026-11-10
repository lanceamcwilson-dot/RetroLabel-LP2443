"""Test-only barcode decoder used by the self-test when ZXing.Net isn't available (sandbox without NuGet). Reads a PGM, prints FORMAT:TEXT lines."""
import sys, zxingcpp
from PIL import Image
for r in zxingcpp.read_barcodes(Image.open(sys.argv[1]), try_rotate=True):
    print(f"{r.format.name}:{r.text}")
