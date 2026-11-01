"""Build a reproducible plugin ZIP without local configuration or credentials."""
import hashlib
import json
from pathlib import Path
from zipfile import ZipFile, ZipInfo, ZIP_DEFLATED
import xml.etree.ElementTree as ET

root = Path(__file__).resolve().parents[1]
version = ET.parse(root / "Directory.Build.props").findtext(".//Version")
source = root / "src/Jellyfin.Plugin.Tipsport/bin/Release/net10.0/Jellyfin.Plugin.Tipsport.dll"
if not source.is_file():
    raise SystemExit("Build the Release configuration before packaging.")
name = "Tipsport_" + version
out = root / ".artifacts"
folder = out / name
folder.mkdir(parents=True, exist_ok=True)
meta = {
    "category": "Live TV", "changelog": "Initial experimental native plugin; one shared provider input.",
    "description": "Unofficial Tipsport catalogue and shared live streaming using your own account.",
    "guid": "7c761c82-7b32-4b82-bb8f-49a6b0f319e7", "name": "Tipsport", "overview": "Native sports catalogue and one shared input",
    "owner": "Nunak", "status": "Active", "targetAbi": "12.4.0.0", "timestamp": "2026-11-01T00:00:00Z", "version": version
}
files = {"Jellyfin.Plugin.Tipsport.dll": source.read_bytes(), "meta.json": (json.dumps(meta, indent=2) + "\n").encode()}
for doc in ("LICENSE", "NOTICE.md", "README.md"):
    files[doc] = (root / doc).read_bytes()
for filename, data in files.items():
    (folder / filename).write_bytes(data)
archive = out / (name + ".zip")
with ZipFile(archive, "w", compression=ZIP_DEFLATED) as bundle:
    for filename, data in sorted(files.items()):
        # Jellyfin extracts into its own versioned directory; DLL must be at ZIP root.
        info = ZipInfo(filename, date_time=(2026, 11, 1, 0, 0, 0))
        info.compress_type = ZIP_DEFLATED
        info.external_attr = 0o100644 << 16
        bundle.writestr(info, data)
digest = hashlib.sha256(archive.read_bytes()).hexdigest()
(out / (archive.name + ".sha256")).write_text(digest + "  " + archive.name + "\n")
manifest = [{
    key: meta[key] for key in ("guid", "name", "description", "overview", "owner", "category")
}]
manifest[0]["versions"] = [{
    "version": version, "changelog": meta["changelog"], "targetAbi": meta["targetAbi"],
    "sourceUrl": "https://github.com/Nunak/jellyfin-plugin-tipsport/releases/download/v" + version + "/" + archive.name,
    # Jellyfin's catalogue protocol requires MD5, independent of the SHA256 release digest.
    "checksum": hashlib.md5(archive.read_bytes(), usedforsecurity=False).hexdigest(),
    "timestamp": meta["timestamp"]
}]
(out / "manifest.candidate.json").write_text(json.dumps(manifest, indent=2) + "\n")
print(archive)
print("SHA256 " + digest)
