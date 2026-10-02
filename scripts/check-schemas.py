#!/usr/bin/env python3
"""Every schemas/<name>/v<N>/examples/valid-*.json must validate and every invalid-*.json must not."""
import glob, json, os, sys
from jsonschema import Draft202012Validator

root = os.path.join(os.path.dirname(__file__), "..", "schemas")
failed = 0
for schema_path in glob.glob(f"{root}/*/v*/*.schema.json"):
    validator = Draft202012Validator(json.load(open(schema_path)))
    for example in sorted(glob.glob(os.path.join(os.path.dirname(schema_path), "examples", "*.json"))):
        ok = validator.is_valid(json.load(open(example)))
        want = os.path.basename(example).startswith("valid-")
        if ok != want:
            failed += 1
            print(f"FAIL {os.path.relpath(example, root)}: expected {'valid' if want else 'invalid'}")
print(f"schemas: {failed} failed")
sys.exit(1 if failed else 0)
