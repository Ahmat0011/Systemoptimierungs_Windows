import json

log_path = r"C:\Users\sahma\.gemini\antigravity\brain\449eabec-4640-4a9f-be2b-d5f4a5708c9d\.system_generated\logs\transcript.jsonl"

with open(log_path, 'r', encoding='utf-8') as f:
    lines = f.readlines()

d = json.loads(lines[630])
content = d.get("content", "")

with open("recovered_step_560.txt", "w", encoding="utf-8") as out:
    out.write(content)

print("Saved recovered content from line 630!")
