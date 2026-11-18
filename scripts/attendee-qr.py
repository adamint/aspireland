# /// script
# requires-python = ">=3.13"
# dependencies = ["segno==1.6.6"]
# ///

import argparse
from pathlib import Path
from urllib.parse import urlsplit

import segno


def main() -> None:
    parser = argparse.ArgumentParser(description="Write a QR code for the attendee site's HTTPS URL.")
    parser.add_argument("url", help="The attendee HTTPS URL printed by devtunnel host")
    parser.add_argument("--output", type=Path, default=Path("attendee-qr.png"), help="PNG path (default: attendee-qr.png)")
    args = parser.parse_args()

    try:
        url = urlsplit(args.url)
    except ValueError:
        parser.error("Pass a valid attendee HTTPS URL.")
    if url.scheme != "https" or not url.hostname or url.username is not None or any(c.isspace() for c in args.url):
        parser.error("Pass a valid attendee HTTPS URL without embedded credentials.")

    try:
        qr = segno.make_qr(args.url, error="m")
        qr.save(args.output, kind="png", scale=12, border=4)
    except segno.DataOverflowError:
        parser.error("The attendee URL is too long for a QR code.")
    except OSError as error:
        parser.exit(1, f"Could not write {args.output}: {error.strerror}\n")
    print(f"Wrote {args.output}")


if __name__ == "__main__":
    main()
