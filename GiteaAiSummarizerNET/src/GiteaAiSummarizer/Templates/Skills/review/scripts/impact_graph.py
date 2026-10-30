"""
name: impact_graph
description: Extracts symbols, routes, and Docker changes from a git diff for impact analysis.
arguments: [diff]
"""
import io
import json
import re
import sys

if sys.platform == "win32":
    sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8")


def extract_impact_candidates(diff):
    symbols = set()
    api_routes = set()
    container_changes = set()

    symbol_patterns = [
        r"^\+\s*(?:(?:public|private|internal|protected|static|async|virtual|override|readonly|partial)\s+)+[\w<>[\]]+\s+([\w\d_]+)\s*\(",
        r"^\+\s*(?:class|interface|record|struct|enum)\s+([\w\d_]+)",
        r"^\+\s*(?:async\s+)?def\s+([\w\d_]+)\s*\(",
        r"^\+\s*class\s+([\w\d_]+)[\(:]",
        r"^\+\s*func\s+(?:\([^)]+\)\s+)?([\w\d_]+)\s*\(",
        r"^\+\s*type\s+([\w\d_]+)\s+(?:struct|interface)",
    ]
    route_patterns = [
        r"^\+\s*\[(?:HttpGet|PostMapping|HttpPut|HttpDelete|HttpPatch|Route)\([\"']([^\"'?]+)[\"']\)\]",
        r"^\+\s*(?:app|endpoints)\.Map(?:Get|Post|Put|Delete)\([\"']([^\"'?]+)[\"']",
        r"^\+\s*@(?:Get|Post|Put|Delete|Patch|Request)Mapping\((?:value\s*=\s*)?[\"']([^\"'?]+)[\"']",
        r"^\+\s*@(?:[\w_.]+\.)?(?:get|post|put|delete|patch|route)\([\"']([^\"'?]+)[\"']",
        r"^\+\s*path\([\"']([^\"'?]+)[\"']",
        r"^\+\s*[\w_.]+\.(?:GET|POST|PUT|DELETE|PATCH|Handle(?:Func)?)\([\"']([^\"'?]+)[\"']",
    ]
    docker_patterns = [
        r"^\+FROM\s+([\w\d./:-]+)",
        r"^\+ENV\s+([\w\d_]+)=",
        r"^\+EXPOSE\s+(\d+)",
        r"^\+ports:\s*-\s*[\"']?(\d+:\d+)[\"']?",
    ]

    for line in diff.splitlines():
        for pattern in symbol_patterns:
            match = re.search(pattern, line)
            if match:
                symbols.add(match.group(1))
        for pattern in route_patterns:
            match = re.search(pattern, line)
            if match:
                api_routes.add(match.group(1))
        for pattern in docker_patterns:
            match = re.search(pattern, line)
            if match:
                container_changes.add(match.group(1))

    return sorted(symbols), sorted(api_routes), sorted(container_changes)


def read_diff():
    try:
        payload = json.loads(sys.stdin.read().strip())
        if isinstance(payload, list) and payload:
            # The advertised schema is a bare array of strings with no field name, so the model may
            # send the whole diff as one element or split it across several. Join rather than take
            # payload[0], so either shape yields the full diff instead of just its first line.
            return "\n".join(str(item) for item in payload)
        if isinstance(payload, dict):
            return str(payload.get("diff") or next(iter(payload.values()), ""))
    except (json.JSONDecodeError, OSError):
        pass
    return sys.argv[1] if len(sys.argv) > 1 else ""


if __name__ == "__main__":
    symbols, routes, containers = extract_impact_candidates(read_diff())
    print("## Impact Candidates Detected")
    for symbol in symbols:
        if symbol not in {"Main", "ToString", "Get", "Post", "Task"}:
            print(f"- {symbol}")
    for route in routes:
        print(f"- API route: `{route}`")
    for container in containers:
        print(f"- Container change: {container}")
    if not symbols and not routes and not containers:
        print("No significant symbols or infrastructure changes detected.")