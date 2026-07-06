import json

log_path = r"C:\Users\sahma\.gemini\antigravity\brain\449eabec-4640-4a9f-be2b-d5f4a5708c9d\.system_generated\logs\transcript.jsonl"

found_replacements = []

with open(log_path, 'r', encoding='utf-8') as f:
    for i, line in enumerate(f):
        try:
            d = json.loads(line)
            tool_calls = d.get("tool_calls", [])
            for tc in tool_calls:
                name = tc.get("name")
                args = tc.get("args", {})
                if name in ["replace_file_content", "multi_replace_file_content"] and "MainWindow.xaml" in args.get("TargetFile", ""):
                    found_replacements.append({
                        "index": i,
                        "step_index": d.get("step_index"),
                        "tool": name,
                        "args": args
                    })
        except Exception as e:
            pass

print(f"Found {len(found_replacements)} replacement calls for MainWindow.xaml:")
for fr in found_replacements:
    print(f"Index: {fr['index']}, Step: {fr['step_index']}, Tool: {fr['tool']}")
    if fr['tool'] == "replace_file_content":
        print(f"  TargetContent: {len(fr['args'].get('TargetContent', ''))} chars, ReplacementContent: {len(fr['args'].get('ReplacementContent', ''))} chars")
    elif fr['tool'] == "multi_replace_file_content":
        chunks = fr['args'].get('ReplacementChunks', [])
        if isinstance(chunks, str):
            try:
                chunks = json.loads(chunks)
            except:
                pass
        if isinstance(chunks, list):
            print(f"  Multi chunks: {len(chunks)}")
            for idx, chunk in enumerate(chunks):
                if isinstance(chunk, dict):
                    print(f"    Chunk {idx}: Target {len(chunk.get('TargetContent', ''))} chars, Replacement {len(chunk.get('ReplacementContent', ''))} chars")
        else:
            print(f"  Multi chunks is of type {type(chunks)}")
