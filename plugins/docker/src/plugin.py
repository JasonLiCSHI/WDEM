import datetime
import json
import os
import shutil
import sys
import tempfile
import uuid
from pathlib import Path


def log(message):
    sys.stderr.write(f"[docker-compose-plugin] {message}\n")
    sys.stderr.flush()


def get_config_path():
    return Path.home() / ".docker" / "config.json"


def check_installed():
    return shutil.which("docker.exe") is not None or shutil.which("docker") is not None


def merge_settings(target, source):
    changed = False
    for key, value in source.items():
        if isinstance(value, dict):
            if key not in target or not isinstance(target[key], dict):
                target[key] = {}
                changed = True
            if merge_settings(target[key], value):
                changed = True
        elif value == "":
            if key in target:
                del target[key]
                changed = True
        elif key not in target or target[key] != value:
            target[key] = value
            changed = True
    return changed


def read_config(config_path, dry_run):
    if not config_path.exists():
        return {}

    try:
        with config_path.open("r", encoding="utf-8") as stream:
            content = json.load(stream)
        if not isinstance(content, dict):
            raise ValueError("Docker configuration root must be a JSON object.")
        return content
    except json.JSONDecodeError:
        timestamp = datetime.datetime.now(datetime.timezone.utc).strftime("%Y%m%d%H%M%S")
        backup_path = config_path.with_name(f"{config_path.name}.corrupted.{timestamp}.{uuid.uuid4().hex[:8]}")
        if not dry_run:
            os.replace(config_path, backup_path)
            log(f"Backed up malformed configuration to {backup_path}")
        return {}


def write_config(config_path, content):
    config_path.parent.mkdir(parents=True, exist_ok=True)
    descriptor, temporary_path = tempfile.mkstemp(
        dir=config_path.parent,
        prefix=f"{config_path.name}.",
        suffix=".tmp",
        text=True,
    )
    try:
        with os.fdopen(descriptor, "w", encoding="utf-8", newline="\n") as stream:
            json.dump(content, stream, indent=2)
            stream.write("\n")
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary_path, config_path)
    except Exception:
        if os.path.exists(temporary_path):
            os.unlink(temporary_path)
        raise


def apply_config(args):
    if not isinstance(args, dict):
        return {"changed": False, "error": "args must be an object"}

    settings = args.get("settings", {})
    dry_run = bool(args.get("dryRun", False))
    if not isinstance(settings, dict):
        return {"changed": False, "error": "settings must be an object"}

    try:
        config_path = Path(get_config_path())
        current = read_config(config_path, dry_run)
        changed = False
        if "compose" not in current or not isinstance(current["compose"], dict):
            current["compose"] = {}
            changed = True
        if merge_settings(current["compose"], settings):
            changed = True

        if not changed:
            return {"changed": False}
        if dry_run:
            log(f"Would update {config_path}")
            return {"changed": True}

        write_config(config_path, current)
        log(f"Updated Docker Compose settings in {config_path}")
        return {"changed": True}
    except Exception as error:
        log(f"Failed to apply configuration: {error}")
        return {"changed": False, "error": str(error)}


def write_response(response):
    sys.stdout.write(json.dumps(response) + "\n")
    sys.stdout.flush()


def main():
    input_data = sys.stdin.read().strip()
    if not input_data:
        write_response({"requestId": "unknown", "error": "No input received"})
        return

    try:
        request = json.loads(input_data)
    except json.JSONDecodeError as error:
        write_response({"requestId": "unknown", "error": f"Invalid JSON: {error}"})
        return

    if not isinstance(request, dict):
        write_response({"requestId": "unknown", "error": "Request must be an object"})
        return

    request_id = request.get("requestId") or "unknown"
    command = request.get("command")
    args = request.get("args", {})

    try:
        if command == "check_installed":
            response = {"requestId": request_id, "installed": check_installed()}
        elif command == "apply":
            response = {"requestId": request_id}
            response.update(apply_config(args))
        else:
            response = {
                "requestId": request_id,
                "error": f"Unknown command: {command}",
            }
    except Exception as error:
        response = {"requestId": request_id, "error": f"Internal error: {error}"}

    write_response(response)


if __name__ == "__main__":
    main()
