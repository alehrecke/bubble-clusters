"""Merge ../core/*.cs into the Grasshopper template -> gh/BubbleSolver.cs (paste this into GH).

Usage:  python gh/build_gh.py      (from the repo root, Pneuhaus/Tools/bubble-clusters)
"""
import pathlib, re

here = pathlib.Path(__file__).resolve().parent
core = here.parent / "core"
order = ["Vec3.cs", "FoamMesh.cs", "MobiusMesher.cs", "Remesher.cs", "FoamSolver.cs", "FoamAnalysis.cs", "ClusterPacker.cs"]  # others appended alphabetically
files = order + sorted(p.name for p in core.glob("*.cs") if p.name not in order)

template = (here / "BubbleSolver.template.cs").read_text(encoding="utf-8")
existing = set(re.findall(r"^using [\w.]+;", template, re.M))
usings, bodies = [], []
for name in files:
    text = (core / name).read_text(encoding="utf-8")
    body = []
    for line in text.splitlines():
        if re.match(r"^using [\w.]+;\s*$", line):
            u = line.strip()
            if u not in existing and u not in usings:
                usings.append(u)
        else:
            body.append(line)
    bodies.append("// ---- core/" + name + " ----\n" + "\n".join(body).strip() + "\n")

out = template.replace("// @CORE_USINGS", "\n".join(usings)).replace("// @CORE", "\n".join(bodies))
(here / "BubbleSolver.cs").write_text(out, encoding="utf-8")
print("wrote", here / "BubbleSolver.cs", f"({len(out.splitlines())} lines)")
