# Hardware research: what is documented, what is assumed

Researched 2026-11-10. "Verified" below means a source I could read; nothing here was tested on a physical LP 2443.

| Question | Finding | Confidence / source |
|---|---|---|
| Print method, resolution, speed | LP 2443: 4" direct thermal, **203 dpi**, 2.5 in/s, serial + parallel, 512 KB flash / 256 KB SRAM, discontinued (replaced by LP 2844) | Reseller listing ([Cybarcode](https://cybarcode.com/zebra/printers/label/desktop/lp2443)). Not a Zebra document. |
| Native language | **EPL2** (Eltron). "Eltron LP2443 / Zebra 2443" is listed in the EPL2 row of a printer-support table | Third party ([StarShip supported printers](https://ask.vtechnologies.com/kb/supported-printers/)). I could not open Zebra's EPL2 guide in full (it was truncated and does not list the 2443 in the part I could read), so **EPL2 is likely but must be proven with the Raw text test**. ZPL is *not* assumed. |
| Printable width | **Not found.** 832 dots (4.09 in) is the LP/TLP 28xx-family figure and is the default, user-editable. A 4.00 in label is 812 dots, which fits either way | Assumption. The test label's frame/rulers show the real margins. |
| Modern Zebra driver | Zebra's current ZDesigner driver (v5.1.17.7415, certified for Windows 11) lists LP/TLP 2824, 2844, 2844-Z, 3842, 3844-Z and **no 2443** | [Zebra release notes](https://www.zebra.com/content/dam/support-dam/en/documentation/unrestricted/release-notes/zdesigner-windows-driver-release-notes-v51177415.pdf) |
| Zebra and USB converters | Zebra does not support USB-to-parallel converters; the converter's own driver creates a virtual port and the printer queue is pointed at it. Old Eltron converter relied on 5 V on pin 18 | [Zebra USB printing notes](https://support.zebra.com/cpws/docs/usb/usb_print.htm) |
| ICUSB1284 in Windows | Appears as a virtual printer port **USB001** (not LPT) | [StarTech community](https://community.startech.com/t/what-port-should-be-selected-when-using-the-icusb128410-in-windows/843) (about the ICUSB128410 sibling) |
| Does the port follow the adapter between USB sockets? | **Unknown.** Neither StarTech nor Microsoft documents it. Windows normally ties USB virtual ports to the device + hub path, so a new `USB00x` on a different socket is plausible | Unverified. The app detects it and can re-point the queue (Diagnostics). |
| Windows 11 driver for ICUSB1284 | Not confirmed from a StarTech/Microsoft document (the datasheet pages I tried were unreachable) | Unverified. If no `USB00x` port appears, download the driver from StarTech's product page. |

## Decision
Primary backend = **RAW EPL2 through the Windows queue**: render PDF → 203 dpi 1-bit bitmap → EPL2 `GW` graphics → `WritePrinter` with datatype `RAW`. No Zebra driver is required (use Microsoft's built-in *Generic / Text Only*), which sidesteps the missing 2443 driver. Secondary backend = **Windows driver (GDI)**, which sends the same 1-bit bitmap via a real driver (e.g. try ZDesigner LP 2844, an EPL2 4" 203 dpi sibling; untested guess).

EPL2 commands used: `N q Q D S ZT/ZB R GW P` plus `A B LO X` on the test label. Details I am relying on from memory of the EPL2 guide, each of which the test label is designed to prove or disprove on your printer:
* `GW` bit sense (default: 0-bits print black; toggle in Settings).
* `Q<len>,<gap>` / continuous `Q<len>,0` / black mark `Q<len>,B<n>`.
* `S` range 1-4 and `D` 0-15 (LP 2443 may accept fewer speeds).
