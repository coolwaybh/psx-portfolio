#!/usr/bin/env python3
"""Deploys Psx.Api's publish-output/ to the MonsterASP-hosted production site over SFTP.

Usage: python3 deploy-psx.py [--workers N] [--skip-publish]

Same safety model as MIS's deploy-mis.py (see its own docstring for the full rationale):
write app_offline.htm -> back up current critical files -> mkdir needed dirs -> upload
everything in parallel (each worker its own SFTP connection, per-file size verification +
retry) -> serial retry of any still-failed files -> a full serial verification pass over
every uploaded file -> roll back any critical file that fails verification, then bring
the site back online regardless of outcome (never leaves it down), exiting non-zero if
anything needed attention.
"""
import argparse
import json
import os
import queue
import subprocess
import sys
import threading
import time

import paramiko

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
REPO_ROOT = os.path.dirname(SCRIPT_DIR)  # /Users/sarfrazahmed/PSX Website
SECRETS_PATH = os.path.join(SCRIPT_DIR, "monsterasp-sftp.secrets.json")
LOCAL_ROOT = os.path.join(REPO_ROOT, "Api", "Psx.Api", "publish-output")
CSPROJ_PATH = os.path.join(REPO_ROOT, "Api", "Psx.Api", "Psx.Api.csproj")
REMOTE_ROOT = "wwwroot"
APP_OFFLINE = f"{REMOTE_ROOT}/app_offline.htm"
BACKUP_ROOT = os.path.join(SCRIPT_DIR, "backups")

EXCLUDE_TOP_LEVEL = {"appsettings.json", "appsettings.Development.json", "appsettings.Production.json",
                     "publish", ".DS_Store"}

CRITICAL_FILES = {"Psx.Api.dll", "Psx.Api.pdb", "Psx.Api.deps.json", "Psx.Api.runtimeconfig.json",
                  "Psx.Api.staticwebassets.endpoints.json", "web.config"}

MAX_RETRIES = 3
RETRY_DELAY_SECONDS = 1.5


def connect():
    with open(SECRETS_PATH) as f:
        creds = json.load(f)
    transport = paramiko.Transport((creds["host"], int(creds["port"])))
    transport.connect(username=creds["user"], password=creds["password"])
    return transport, paramiko.SFTPClient.from_transport(transport)


def remote_exists(sftp, path):
    try:
        sftp.stat(path)
        return True
    except FileNotFoundError:
        return False


def remote_size(sftp, path):
    try:
        return sftp.stat(path).st_size
    except FileNotFoundError:
        return None


def remote_mkdirs(sftp, path):
    parts = path.split("/")
    cur = ""
    for p in parts:
        cur = p if not cur else cur + "/" + p
        if not remote_exists(sftp, cur):
            sftp.mkdir(cur)


def scan_local_files():
    files = []
    dirs_needed = set()
    for entry in sorted(os.listdir(LOCAL_ROOT)):
        if entry in EXCLUDE_TOP_LEVEL:
            continue
        local_path = os.path.join(LOCAL_ROOT, entry)
        remote_path = f"{REMOTE_ROOT}/{entry}"
        if os.path.isdir(local_path):
            for dirpath, _dirnames, filenames in os.walk(local_path):
                rel = os.path.relpath(dirpath, LOCAL_ROOT).replace(os.sep, "/")
                remote_dir = f"{REMOTE_ROOT}/{rel}"
                dirs_needed.add(remote_dir)
                for fn in filenames:
                    if fn == ".DS_Store":
                        continue
                    lp = os.path.join(dirpath, fn)
                    files.append((lp, f"{remote_dir}/{fn}", os.path.getsize(lp)))
        else:
            files.append((local_path, remote_path, os.path.getsize(local_path)))
    return files, dirs_needed


def upload_one(local_path, remote_path, local_size):
    last_err = None
    for attempt in range(1, MAX_RETRIES + 1):
        transport = sftp = None
        try:
            transport, sftp = connect()
            sftp.put(local_path, remote_path)
            actual = remote_size(sftp, remote_path)
            if actual == local_size:
                return True, None
            last_err = f"size mismatch after upload: local={local_size} remote={actual}"
        except Exception as e:
            last_err = str(e)
        finally:
            if sftp:
                sftp.close()
            if transport:
                transport.close()
        if attempt < MAX_RETRIES:
            time.sleep(RETRY_DELAY_SECONDS)
    return False, last_err


def parallel_upload(files, num_workers):
    q = queue.Queue()
    for item in files:
        q.put(item)
    results_lock = threading.Lock()
    failed = []
    done_count = [0]
    total = len(files)

    def worker():
        while True:
            try:
                local_path, remote_path, size = q.get_nowait()
            except queue.Empty:
                return
            ok, err = upload_one(local_path, remote_path, size)
            with results_lock:
                done_count[0] += 1
                if not ok:
                    failed.append((local_path, remote_path, err))
                if done_count[0] % 10 == 0 or done_count[0] == total:
                    print(f"  {done_count[0]}/{total} uploaded ({len(failed)} failed so far)")

    threads = [threading.Thread(target=worker) for _ in range(num_workers)]
    for t in threads:
        t.start()
    for t in threads:
        t.join()
    return failed


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--workers", type=int, default=6, help="Concurrent upload connections (default 6)")
    ap.add_argument("--skip-publish", action="store_true", help="Use the existing publish-output/ as-is")
    args = ap.parse_args()

    if not args.skip_publish:
        print("Running dotnet publish -c Release...")
        r = subprocess.run(["dotnet", "publish", CSPROJ_PATH, "-c", "Release", "-o", LOCAL_ROOT])
        if r.returncode != 0:
            print("dotnet publish failed - aborting, nothing touched on the server.")
            sys.exit(1)

    files, dirs_needed = scan_local_files()
    print(f"{len(files)} local file(s) to upload across {len(dirs_needed)} director{'y' if len(dirs_needed) == 1 else 'ies'}.")

    main_transport, main_sftp = connect()
    try:
        with main_sftp.open(APP_OFFLINE, "w") as f:
            f.write("<html><body>Deploying an update, back in a moment...</body></html>")
        print("Wrote app_offline.htm - site taken offline for deploy.")
        time.sleep(5)

        backup_dir = os.path.join(BACKUP_ROOT, time.strftime("%Y%m%d-%H%M%S"))
        os.makedirs(backup_dir, exist_ok=True)
        backed_up = []
        for name in CRITICAL_FILES:
            rp = f"{REMOTE_ROOT}/{name}"
            if remote_exists(main_sftp, rp):
                main_sftp.get(rp, os.path.join(backup_dir, name))
                backed_up.append(name)
        print(f"Backed up {len(backed_up)} critical file(s) to {backup_dir}")

        for d in sorted(dirs_needed):
            remote_mkdirs(main_sftp, d)

        print(f"Uploading with {args.workers} parallel connection(s)...")
        start = time.time()
        failed = parallel_upload(files, args.workers)
        print(f"Parallel phase done in {time.time() - start:.0f}s ({len(failed)} failed).")

        if failed:
            print(f"Retrying {len(failed)} failed file(s) serially on the main connection...")
            still_failed = []
            for local_path, remote_path, _err in failed:
                try:
                    main_sftp.put(local_path, remote_path)
                    if remote_size(main_sftp, remote_path) != os.path.getsize(local_path):
                        still_failed.append((local_path, remote_path))
                except Exception:
                    still_failed.append((local_path, remote_path))
            failed = still_failed

        print("Final integrity pass - verifying every uploaded file's size against local...")
        mismatches = []
        for local_path, remote_path, local_size in files:
            actual = remote_size(main_sftp, remote_path)
            if actual != local_size:
                mismatches.append((local_path, remote_path, local_size, actual))

        if not mismatches:
            print(f"Verified: all {len(files)} file(s) match local size exactly.")
        else:
            critical_bad = [m for m in mismatches if os.path.basename(m[1]) in CRITICAL_FILES]
            noncritical_bad = [m for m in mismatches if os.path.basename(m[1]) not in CRITICAL_FILES]

            if critical_bad:
                print(f"\n*** {len(critical_bad)} CRITICAL file(s) failed verification - rolling those back to the pre-deploy backup: ***")
                for local_path, remote_path, local_size, actual in critical_bad:
                    name = os.path.basename(remote_path)
                    print(f"  {remote_path}: local={local_size} remote={actual}")
                    backup_file = os.path.join(backup_dir, name)
                    if os.path.exists(backup_file):
                        main_sftp.put(backup_file, remote_path)
                        print(f"    -> restored previous version from backup")
                    else:
                        print(f"    -> NO BACKUP AVAILABLE for {name} - left as-is, needs manual attention")

            if noncritical_bad:
                print(f"\n{len(noncritical_bad)} non-critical file(s) failed verification (site will still run, but these need a re-run):")
                for _local_path, remote_path, local_size, actual in noncritical_bad:
                    print(f"  {remote_path}: local={local_size} remote={actual}")

        main_sftp.remove(APP_OFFLINE)
        print("\nRemoved app_offline.htm - site back online.")

        if mismatches:
            print(f"\nDEPLOY COMPLETED WITH {len(mismatches)} PROBLEM FILE(S) - see above.")
            sys.exit(1)
        else:
            print("\nDeploy completed cleanly.")
    finally:
        main_sftp.close()
        main_transport.close()


if __name__ == "__main__":
    main()
