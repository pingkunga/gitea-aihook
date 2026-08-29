"""
name: impact_graph
description: Extracts symbols, routes, and Docker changes from a git diff for impact analysis.
arguments: [diff]
"""
import sys
import io

# บังคับ stdout ให้เป็น utf-8 เพื่อรองรับ Emoji บน Windows
if sys.platform == "win32":
    sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8")

import re

def extract_impact_candidates(diff):
    symbols = set()
    api_routes = set()
    container_changes = set()

    # --- 1. Symbols (Functions/Methods/Classes) ---
    symbol_patterns = [
        # .NET & Spring
        r'^\+\s*(?:(?:public|private|internal|protected|static|async|virtual|override|readonly|partial)\s+)+[\w<>[\]]+\s+([\w\d_]+)\s*\(',
        r'^\+\s*(?:class|interface|record|struct|enum)\s+([\w\d_]+)',
        # Python
        r'^\+\s*(?:async\s+)?def\s+([\w\d_]+)\s*\(',
        r'^\+\s*class\s+([\w\d_]+)[\(:]',
        # Go
        r'^\+\s*func\s+(?:\([^)]+\)\s+)?([\w\d_]+)\s*\(',
        r'^\+\s*type\s+([\w\d_]+)\s+(?:struct|interface)'
    ]

    # --- 2. API Routes ---
    route_patterns = [
        # .NET
        r'^\+\s*\[(?:HttpGet|PostMapping|HttpPut|HttpDelete|HttpPatch|Route)\(["\']([^"\'\?]+)["\']\)\]',
        r'^\+\s*(?:app|endpoints)\.Map(?:Get|Post|Put|Delete)\(["\']([^"\'\?]+)["\']',
        # Spring
        r'^\+\s*@(?:Get|Post|Put|Delete|Patch|Request)Mapping\((?:value\s*=\s*)?["\']([^"\'\?]+)["\']',
        # Python
        r'^\+\s*@(?:[\w_.]+\.)?(?:get|post|put|delete|patch|route)\(["\']([^"\'\?]+)["\']',
        r'^\+\s*path\(["\']([^"\'\?]+)["\']',
        # Go
        r'^\+\s*[\w_.]+\.(?:GET|POST|PUT|DELETE|PATCH|Handle(?:Func)?)\(["\']([^"\'\?]+)["\']'
    ]

    # --- 3. Docker/Container Changes ---
    docker_patterns = [
        r'^\+FROM\s+([\w\d./:-]+)',
        r'^\+ENV\s+([\w\d_]+)=',
        r'^\+EXPOSE\s+(\d+)',
        r'^\+ports:\s*-\s*["\']?(\d+:\d+)["\']?'
    ]

    for line in diff.splitlines():
        for p in symbol_patterns:
            m = re.search(p, line)
            if m: symbols.add(m.group(1))
        
        for p in route_patterns:
            m = re.search(p, line)
            if m: api_routes.add(m.group(1))

        for p in docker_patterns:
            m = re.search(p, line)
            if m: container_changes.add(m.group(1))

    return sorted(list(symbols)), sorted(list(api_routes)), sorted(list(container_changes))

if __name__ == "__main__":
    import json
    
    diff_content = ""
    
    # 1. Try reading from Stdin (JSON format)
    try:
        raw_input = sys.stdin.read().strip()
        if raw_input:
            data = json.loads(raw_input)
            
            # Microsoft Agents SDK provides arguments as either an array or an object
            if isinstance(data, list) and len(data) > 0:
                diff_content = data[0]
            elif isinstance(data, dict):
                # Check for "diff" key or the first value in the dict
                diff_content = data.get("diff")
                if diff_content is None and len(data) > 0:
                    diff_content = next(iter(data.values()))
    except Exception:
        pass

    # 2. Fallback to CLI arguments (useful for local testing)
    if not diff_content and len(sys.argv) > 1:
        diff_content = sys.argv[1]

    # Defensive check: Ensure diff_content is a string and not a nested list/dict
    if isinstance(diff_content, (list, dict)):
        # If it's still a list, try to get the first element as a string
        if isinstance(diff_content, list) and len(diff_content) > 0:
            diff_content = str(diff_content[0])
        else:
            diff_content = str(diff_content)
    
    # Ensure it's not None
    diff_content = diff_content or ""

    if not diff_content:
        print("No diff content provided via Stdin or arguments.")
        sys.exit(0)

    symbols, routes, containers = extract_impact_candidates(diff_content)

    print("## 🔍 Impact Candidates Detected")
    if symbols:
        print("\n### 🏷️ Symbols (Search these for references):")
        for s in symbols:
            if s not in ["Main", "ToString", "Get", "Post", "Task"]:
                print(f"- {s}")
    
    if routes:
        print("\n### 🌐 API Routes (Potential breaking changes):")
        for r in routes:
            print(f"- `{r}`")

    if containers:
        print("\n### 🐳 Infrastructure/Docker Changes:")
        for c in containers:
            print(f"- {c}")

    if not symbols and not routes and not containers:
        print("No significant symbols or infrastructure changes detected.")
