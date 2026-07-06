import json

log_path = r"C:\Users\sahma\.gemini\antigravity\brain\449eabec-4640-4a9f-be2b-d5f4a5708c9d\.system_generated\logs\transcript.jsonl"

with open(log_path, 'r', encoding='utf-8') as f:
    lines = f.readlines()

d = json.loads(lines[50])
print(f"Step {d.get('step_index')}: {d.get('type')}")
tool_calls = d.get("tool_calls", [])
for tc in tool_calls:
    args = tc.get("args", {})
    chunks = args.get("ReplacementChunks", "")
    if isinstance(chunks, str):
        try:
            chunks = json.loads(chunks)
        except Exception as e:
            print("Failed to parse chunks string:", e)
    print(f"  Tool: {tc.get('name')}, TargetFile: {args.get('TargetFile')}, Chunks type: {type(chunks)}, num chunks: {len(chunks) if isinstance(chunks, list) else 'N/A'}")
