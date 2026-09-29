#!/usr/bin/env python3
"""Release zip (Krishna's standing rule): Evict-<version>.zip holds exactly two folders.

    installer/  Evict-Setup-<version>.exe + .sha256   (installs Evict)
    portable/   Evict.exe + .sha256                    (runs from anywhere)

    python build/release_zip.py 1.7.0 --setup Installed --exe Portable --out Zip
        -> Zip/Evict-1.7.0.zip and Zip/Evict-1.7.0.zip.sha256
    python build/release_zip.py --check Zip/Evict-1.7.0.zip

It refuses to write a zip with anything else in it. Needs Python 3 only (stdlib).
"""
import argparse
import glob
import hashlib
import os
import sys
import zipfile

FOLDERS = ("installer", "portable")


class ZipError(Exception):
    pass


def sha256_file(file):
    h = hashlib.sha256()
    with open(file, "rb") as f:
        for block in iter(lambda: f.read(1 << 20), b""):
            h.update(block)
    return h.hexdigest().upper()


def check_zip(file):
    """Only installer/ and portable/ at the top, and something in each."""
    with zipfile.ZipFile(file) as z:
        names = [n.replace("\\", "/") for n in z.namelist()]
    top = {n.split("/")[0] for n in names}
    loose = [n for n in names if "/" not in n]
    if top != set(FOLDERS) or loose:
        raise ZipError(f"{file}: top level must be exactly {', '.join(FOLDERS)}; found {sorted(top)}")


def make_zip(version, setup_dir, exe_dir, out_dir):
    setups = sorted(glob.glob(os.path.join(setup_dir, "*.exe")))
    if len(setups) != 1:
        raise ZipError(f"expected one setup EXE in {setup_dir}, found {len(setups)}")
    exe = os.path.join(exe_dir, "Evict.exe")
    if not os.path.isfile(exe):
        raise ZipError(f"{exe} not found")
    os.makedirs(out_dir, exist_ok=True)
    out = os.path.join(out_dir, f"Evict-{version}.zip")
    with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as z:
        for folder, file in (("installer", setups[0]), ("portable", exe)):
            name = os.path.basename(file)
            z.write(file, f"{folder}/{name}")
            z.writestr(f"{folder}/{name}.sha256", sha256_file(file) + "\r\n")
    check_zip(out)
    with open(out + ".sha256", "w", encoding="ascii", newline="\n") as f:
        f.write(sha256_file(out) + "\n")
    return out


def main(argv=None):
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("version", nargs="?")
    p.add_argument("--setup", help="folder with the one Evict-Setup-*.exe (Installed)")
    p.add_argument("--exe", help="folder with Evict.exe (Portable)")
    p.add_argument("--out", help="folder for Evict-<version>.zip")
    p.add_argument("--check", metavar="ZIP", help="only check an existing zip")
    a = p.parse_args(argv)
    try:
        if a.check:
            check_zip(a.check)
            print(f"{a.check}: installer/ and portable/ only")
        elif a.version and a.setup and a.exe and a.out:
            print(make_zip(a.version, a.setup, a.exe, a.out))
        else:
            p.error("give <version> --setup --exe --out, or --check ZIP")
    except ZipError as e:
        print("release_zip: " + str(e), file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
