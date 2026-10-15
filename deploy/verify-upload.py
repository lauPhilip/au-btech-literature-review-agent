"""Checks a deploy on the server: every file of the build must be there with the same size.

The deploy job runs this after lftp has mirrored ./publish to the server and before it removes app_offline.htm.
lftp's mirror skips a file it thinks is unchanged, and on 15 October 2026 the server held empty copies of .NET
runtime files (System.Runtime.InteropServices.dll, Microsoft.Win32.Registry.dll) that every later deploy left in
place, so the site would not start (HTTP 502.5). This script asks the server for the size of each file, uploads
again every file that is missing or has another size, and checks those once more. It exits with 1 when a file
still differs, so the job fails and the "Updating" page stays up instead of a broken app starting.

Settings come from the job's environment: FTP_HOST, FTP_USER, LFTP_PASSWORD and REMOTE_DIR, and the folder of
the build as the first argument (./publish). The password is never printed.
"""

import ftplib
import os
import posixpath
import ssl
import sys


def connect() -> ftplib.FTP_TLS:
    ftp = ftplib.FTP_TLS(os.environ["FTP_HOST"], context=ssl.create_default_context(), timeout=60)
    ftp.login(os.environ["FTP_USER"], os.environ["LFTP_PASSWORD"])
    ftp.prot_p()  # encrypt the data connection too
    ftp.voidcmd("TYPE I")  # sizes in bytes
    return ftp


# What the mirror leaves alone on the server is never checked or uploaded here either.
KEPT_ON_SERVER = ("web.config", "App_Data/", "WorkspaceStore/")


def local_files(root: str) -> dict[str, int]:
    """Every file of the build, by its path relative to the build folder (with /), and its size."""
    files = {}
    for folder, _, names in os.walk(root):
        for name in names:
            path = os.path.join(folder, name)
            rel = os.path.relpath(path, root).replace(os.sep, "/")
            if rel == KEPT_ON_SERVER[0] or rel.startswith(KEPT_ON_SERVER[1:]):
                continue
            files[rel] = os.path.getsize(path)
    return files


def remote_size(ftp: ftplib.FTP_TLS, path: str) -> int | None:
    try:
        return ftp.size(path)
    except ftplib.error_perm:
        return None  # not on the server


def differing(ftp: ftplib.FTP_TLS, remote_dir: str, files: dict[str, int]) -> list[str]:
    return [rel for rel, size in sorted(files.items()) if remote_size(ftp, posixpath.join(remote_dir, rel)) != size]


def upload(ftp: ftplib.FTP_TLS, root: str, remote_dir: str, rel: str) -> None:
    target = posixpath.join(remote_dir, rel)
    # Make any missing folders on the way; an existing folder answers with an error, which is fine.
    parts = posixpath.dirname(rel).split("/") if posixpath.dirname(rel) else []
    for i in range(len(parts)):
        try:
            ftp.mkd(posixpath.join(remote_dir, *parts[: i + 1]))
        except ftplib.error_perm:
            pass
    with open(os.path.join(root, *rel.split("/")), "rb") as f:
        ftp.storbinary(f"STOR {target}", f)


def main() -> int:
    root = sys.argv[1] if len(sys.argv) > 1 else "./publish"
    remote_dir = os.environ.get("REMOTE_DIR", "/public_html")
    files = local_files(root)
    ftp = connect()
    try:
        wrong = differing(ftp, remote_dir, files)
        print(f"Checked {len(files)} files on the server; {len(wrong)} missing or of another size.")
        for rel in wrong:
            print(f"  uploading again: {rel} ({files[rel]} bytes)")
            upload(ftp, root, remote_dir, rel)
        still = differing(ftp, remote_dir, {rel: files[rel] for rel in wrong})
    finally:
        try:
            ftp.quit()
        except (OSError, ftplib.Error):
            ftp.close()
    for rel in still:
        print(f"::error::{rel} on the server still differs from the build ({files[rel]} bytes).")
    return 1 if still else 0


if __name__ == "__main__":
    sys.exit(main())
