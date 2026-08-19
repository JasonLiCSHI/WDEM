
import sys
import json
import os
import tempfile
from pathlib import Path

def deep_merge(dict1, dict2):
    """Recursively deep merge dict2 into dict1."""
    for key, value in dict2.items():
        if isinstance(value, dict) and key in dict1 and isinstance(dict1[key], dict):
            deep_merge(dict1[key], value)
        else:
            dict1[key] = value
    return dict1

def get_config_path():
    """Resolve cross-platform Docker config path."""
    return Path.home() / ".docker" / "config.json"

def main():
    input_data = sys.stdin.read().strip()
    
    # 1. Empty stdin returns JSON error
    if not input_data:
        print(json.dumps({"error": "Empty stdin"}))
        sys.exit(1)

    try:
        request = json.loads(input_data)
    except json.JSONDecodeError as e:
        print(json.dumps({"error": f"Invalid JSON format: {str(e)}"}))
        sys.exit(1)

    request_id = request.get("requestId")
    action = request.get("action")
    args = request.get("args", {})

    # 2. check_installed bare bool
    if action == "check_installed":
        print("true")
        return

    if action == "apply":
        settings = args.get("settings", {})
        dry_run = args.get("dryRun", False)
        
        config_path = get_config_path()
        existing_data = {}
        
        if config_path.exists():
            try:
                with open(config_path, "r", encoding="utf-8") as f:
                    existing_data = json.load(f)
            except Exception:
                pass  # Fallback to empty dict if corrupted
                
        if "compose" not in existing_data or not isinstance(existing_data["compose"], dict):
            existing_data["compose"] = {}
            
        # 3. Deep-merge compose settings
        deep_merge(existing_data["compose"], settings)
        
        if not dry_run:
            config_path.parent.mkdir(parents=True, exist_ok=True)
            
            # 4. Atomic writes via tempfile.mkstemp + os.replace
            fd, temp_path = tempfile.mkstemp(dir=config_path.parent, text=True)
            try:
                with os.fdopen(fd, "w", encoding="utf-8") as f:
                    json.dump(existing_data, f, indent=2)
                    # 5. POSIX trailing newlines
                    f.write("\n")
                os.replace(temp_path, config_path)
            except Exception as e:
                if os.path.exists(temp_path):
                    os.unlink(temp_path)
                print(json.dumps({"requestId": request_id, "error": str(e)}))
                sys.exit(1)

        # 6. Protocol compliance: No success/data fields
        print(json.dumps({"requestId": request_id}))
        return

    print(json.dumps({"requestId": request_id, "error": f"Unknown action: {action}"}))
    sys.exit(1)

if __name__ == "__main__":
    main()
  

import datetime
import json
import os
import shutil
import sys
import uuid


def log(msg):
    sys.stderr.write(f"[docker-plugin] {msg}\n")
    sys.stderr.flush()


def get_config_path():
    appdata = os.getenv("APPDATA")
    if not appdata:
        raise Exception("APPDATA environment variable not found")

    config_dir = os.path.join(appdata, "Docker")
    return os.path.join(config_dir, "settings.json")


def read_json(file_path: str) -> dict:
    if not os.path.exists(file_path):
        return {}

    try:
        with open(file_path, "r", encoding="utf-8") as f:
            data = json.load(f)
            return data if isinstance(data, dict) else {}
    except json.JSONDecodeError:
        timestamp = datetime.datetime.now(datetime.timezone.utc).strftime("%Y%m%d%H%M%S")
        suffix = uuid.uuid4().hex[:8]
        backup_path = f"{file_path}.corrupted.{timestamp}.{suffix}"
        log(f"Config corrupted. Backing up to {backup_path} and starting fresh.")
        try:
            shutil.move(file_path, backup_path)
        except Exception as backup_e:
            log(f"Failed to backup corrupted config: {backup_e}")
        return {}
    except OSError as e:
        raise OSError(f"Could not read {file_path}: {e}") from e


def write_json(file_path: str, data: dict) -> None:
    os.makedirs(os.path.dirname(file_path), exist_ok=True)
    temp_path = file_path + ".tmp"
    with open(temp_path, "w", encoding="utf-8") as f:
        json.dump(data, f, indent=2)
    os.replace(temp_path, file_path)


def merge_settings(target: dict, source: dict) -> bool:
    changed = False
    for key, value in source.items():
        if isinstance(value, dict):
            if key not in target or not isinstance(target.get(key), dict):
                target[key] = {}
                changed = True

            # Recursive merge for deep dictionaries
            if merge_settings(target[key], value):
                changed = True
        else:
            if value == "":
                if key in target:
                    del target[key]
                    changed = True
            elif key not in target or target[key] != value:
                target[key] = value
                changed = True
    return changed


def check_installed(args: dict, request_id: str) -> dict:
    # Check for docker or docker.exe in PATH
    installed = shutil.which("docker.exe") is not None or shutil.which("docker") is not None
    return {
        "requestId": request_id,
        "success": True,
        "changed": False,
        "data": installed,
    }


def apply_config(args: dict, context: dict, request_id: str) -> dict:
    dry_run = context.get("dryRun", False)
    settings = args.get("settings", {})

    try:
        config_path = get_config_path()
        current_config = read_json(config_path)

        changed = merge_settings(current_config, settings)

        if not changed:
            return {
                "requestId": request_id,
                "success": True,
                "changed": False,
            }

        if dry_run:
            log(f"Would update {config_path} with new settings")
            return {
                "requestId": request_id,
                "success": True,
                "changed": changed,
            }

        write_json(config_path, current_config)
        log(f"Updated Docker config: {config_path}")

        return {
            "requestId": request_id,
            "success": True,
            "changed": True,
        }

    except Exception as e:
        log(f"Failed to apply config: {e}")
        return {
            "requestId": request_id,
            "success": False,
            "changed": False,
            "error": str(e),
        }


def main():
    input_data = sys.stdin.read()
    if not input_data:
        return

    try:
        request = json.loads(input_data)
    except Exception as e:
        log(f"Failed to parse request: {e}")
        response = {
            "requestId": "unknown",
            "success": False,
            "changed": False,
            "error": f"Failed to parse request: {str(e)}",
        }
        sys.stdout.write(json.dumps(response) + "\n")
        sys.stdout.flush()
        return

    request_id = request.get("requestId", "unknown")
    command = request.get("command")
    args = request.get("args", {})
    context = request.get("context", {})

    response = {
        "requestId": request_id,
        "success": False,
        "changed": False,
    }

    try:
        if command == "check_installed":
            response = check_installed(args, request_id)
        elif command == "apply":
            response = apply_config(args, context, request_id)
        else:
            response["error"] = f"Unknown command: {command}"
    except Exception as fatal_err:
        response["error"] = f"Internal Script Error: {str(fatal_err)}"

    sys.stdout.write(json.dumps(response) + "\n")
    sys.stdout.flush()


if __name__ == "__main__":
    main()

