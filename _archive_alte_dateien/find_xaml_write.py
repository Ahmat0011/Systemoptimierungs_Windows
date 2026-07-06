import json

log_path = r"C:\Users\sahma\.gemini\antigravity\brain\449eabec-4640-4a9f-be2b-d5f4a5708c9d\.system_generated\logs\transcript.jsonl"

found_writes = []

with open(log_path, 'r', encoding='utf-8') as f:
    for i, line in enumerate(f):
        try:
            d = json.loads(line)
            # Check for PLANNER_RESPONSE containing tool calls
            tool_calls = d.get("tool_calls", [])
            for tc in tool_calls:
                name = tc.get("name")
                args = tc.get("args", {})
                if name == "write_to_file" and "MainWindow.xaml" in args.get("TargetFile", ""):
                    found_writes.append({
                        "index": i,
                        "step_index": d.get("step_index"),
                        "args": args
                    })
        except Exception as e:
            pass

print(f"Found {len(found_writes)} write_to_file calls for MainWindow.xaml:")
for fw in found_writes:
    print(f"Index: {fw['index']}, Step: {fw['step_index']}, Target: {fw['args'].get('TargetFile')}, Code len: {len(fw['args'].get('CodeContent', ''))}")
