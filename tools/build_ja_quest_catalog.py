#!/usr/bin/env python3
import json, sys
from pathlib import Path

def load(p):
    with open(p, "r", encoding="utf-8-sig") as f:
        return json.load(f)

def pick(d, keys):
    for k in keys:
        if k in d and d[k] not in (None, "", 0):
            return d[k]
    return None

if len(sys.argv) != 5:
    print("usage: build_ja_quest_catalog.py TextMapJP.json MainQuestExcelConfigData.json QuestExcelConfigData.json output.json")
    sys.exit(2)

textmap = load(sys.argv[1])
mainq = load(sys.argv[2])
quest = load(sys.argv[3])

def txt(h):
    if h is None:
        return None
    return textmap.get(str(h)) or textmap.get(h)

parents=[]
for x in mainq:
    pid=pick(x,["id","mainId","mainQuestId"])
    if pid is None:
        continue
    title_hash=pick(x,["titleTextMapHash","nameTextMapHash"])
    desc_hash=pick(x,["descTextMapHash"])
    parents.append({
        "parent_quest_id": int(pid),
        "type": x.get("type"),
        "title_ja": txt(title_hash),
        "description_ja": txt(desc_hash),
        "title_textmap_hash": title_hash,
        "description_textmap_hash": desc_hash,
        "chapter_id": x.get("chapterId"),
        "show_type": x.get("showType"),
        "repeatable": x.get("repeatable"),
    })

children=[]
for x in quest:
    qid=pick(x,["subId","id","questId"])
    pid=pick(x,["mainId","mainQuestId","parentQuestId"])
    if qid is None:
        continue
    desc_hash=pick(x,["descTextMapHash","titleTextMapHash","nameTextMapHash"])
    children.append({
        "quest_id": int(qid),
        "parent_quest_id": int(pid) if pid is not None else None,
        "description_ja": txt(desc_hash),
        "description_textmap_hash": desc_hash,
        "order": x.get("order"),
    })

diag={
    "main_count": len(mainq),
    "quest_count": len(quest),
    "parent_count": len(parents),
    "child_count": len(children),
    "parent_named": sum(1 for x in parents if x["title_ja"]),
    "child_named": sum(1 for x in children if x["description_ja"]),
    "main_keys_sample": sorted(mainq[0].keys()) if mainq else [],
    "quest_keys_sample": sorted(quest[0].keys()) if quest else [],
}

out={
    "info":{
        "schema":"jipi_genshin_quest_catalog_ja_v1",
        "game_version":"7.1",
        "source":"Dimbreath/animegamedata2",
        "language":"ja",
        "note":"固定の日本語名称カタログ。優先順位・現在判断は含めない。"
    },
    "diagnostics":diag,
    "parents":parents,
    "children":children
}
Path(sys.argv[4]).write_text(json.dumps(out,ensure_ascii=False,indent=2),encoding="utf-8")
print(json.dumps(diag,ensure_ascii=False))
