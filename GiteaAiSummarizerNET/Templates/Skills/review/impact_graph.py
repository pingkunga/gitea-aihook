import sys
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
    if len(sys.argv) < 2:
        print("No diff content provided.")
        sys.exit(0)

    diff_content = sys.argv[1]
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
