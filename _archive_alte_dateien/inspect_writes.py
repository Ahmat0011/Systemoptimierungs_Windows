import json

log_path = r"C:\Users\sahma\.gemini\antigravity\brain\449eabec-4640-4a9f-be2b-d5f4a5708c9d\.system_generated\logs\transcript.jsonl"

with open(log_path, 'r', encoding='utf-8') as f:
    lines = f.readlines()

for idx in [498, 988]:
    try:
        d = json.loads(lines[idx])
        tool_calls = d.get("tool_calls", [])
        for tc in tool_calls:
            name = tc.get("name")
            args = tc.get("args", {})
            print(f"Index {idx}: Tool {name}, TargetFile: {args.get('TargetFile')}")
            content_raw = args.get("CodeContent", "")
            print(f"  Raw Content length: {len(content_raw)}")
            if len(content_raw) > 100:
                print(f"  Start: {content_raw[:100]}")
                print(f"  End: {content_raw[-100:]}")
    except Exception as e:
        print(f"Error parsing index {idx}: {e}")
