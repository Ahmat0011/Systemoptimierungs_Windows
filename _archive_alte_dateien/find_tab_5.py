import json

log_path = r"C:\Users\sahma\.gemini\antigravity\brain\449eabec-4640-4a9f-be2b-d5f4a5708c9d\.system_generated\logs\transcript.jsonl"

with open(log_path, 'r', encoding='utf-8') as f:
    lines = f.readlines()

d = json.loads(lines[485])
tool_calls = d.get("tool_calls", [])
for tc in tool_calls:
    if tc.get("name") == "replace_file_content":
        content_raw = tc["args"]["ReplacementContent"]
        
        # Decode using unicode_escape
        content = content_raw.encode('utf-8').decode('unicode_escape')
        
        # Strip leading and trailing quotes if any
        content = content.strip('"')
            
        with open("tab_5_content.txt", "w", encoding="utf-8") as out:
            out.write(content)
        print(f"Successfully extracted and decoded Tab 5, stripped length: {len(content)}")
