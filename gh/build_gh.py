"""Merge gh/BubbleEngine.cs and ../core/*.cs into the Grasshopper template -> gh/BubbleSolver.cs
(paste this into a Rhino 8 C# Script component). The compiled plugin (plugin/) uses the same files.

Usage:  python gh/build_gh.py      (from the repo root, Pneuhaus/Tools/bubble-clusters)
"""
import pathlib, re

here = pathlib.Path(__file__).resolve().parent
core = here.parent / "core"
order = ["Vec3.cs", "FoamMesh.cs", "MobiusMesher.cs", "Remesher.cs", "FoamSolver.cs", "FoamAnalysis.cs", "ClusterPacker.cs"]  # others appended alphabetically
files = [core / n for n in order] + sorted(p for p in core.glob("*.cs") if p.name not in order)

template = (here / "BubbleSolver.template.cs").read_text(encoding="utf-8")
existing = set(re.findall(r"^using [\w.]+;", template, re.M))
usings = []


def strip(text):
    """Body without its using lines (collected into `usings` unless the template already has them)."""
    body = []
    for line in text.splitlines():
        if re.match(r"^using [\w.]+;\s*$", line):
            u = line.strip()
            if u not in existing and u not in usings:
                usings.append(u)
        elif line.strip() in ("#region Usings", "#endregion"):
            continue
        else:
            body.append(line)
    return "\n".join(body).strip() + "\n"


engine = strip((here / "BubbleEngine.cs").read_text(encoding="utf-8"))
bodies = ["// ---- core/" + p.name + " ----\n" + strip(p.read_text(encoding="utf-8")) for p in files]

out = (template.replace("// @CORE_USINGS", "\n".join(usings))
               .replace("// @ENGINE", engine)
               .replace("// @CORE", "\n".join(bodies)))
(here / "BubbleSolver.cs").write_text(out, encoding="utf-8")
print("wrote", here / "BubbleSolver.cs", f"({len(out.splitlines())} lines)")
