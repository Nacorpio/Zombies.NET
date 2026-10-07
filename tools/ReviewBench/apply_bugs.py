import json,sys,pathlib
root=pathlib.Path(sys.argv[1]); ids=set(sys.argv[2].split(",")) if len(sys.argv)>2 and sys.argv[2]!="all" else None
bugs=json.load(open(pathlib.Path(__file__).parent/"bugs.json"))
for b in bugs:
    if ids and b["id"] not in ids: continue
    p=root/b["file"]; s=p.read_text()
    assert s.count(b["old"])==1, (b["id"], s.count(b["old"]))
    p.write_text(s.replace(b["old"],b["new"]))
print("applied", ids or "all")
