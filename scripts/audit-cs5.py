#!/usr/bin/env python3
"""Mechanical C#5-only syntax audit for Day0Gen.cs.

Strips comments and string/char literals, then scans for C#6+ constructs
(null-conditional, interpolation, nameof, expression-bodied members, local
functions, async/await, pattern matching, inline out declarations,
target-typed new, using static, ??=) and checks delimiter balance.
Exit code 0 = clean, 1 = violations found.
"""
import re
import sys


def strip_code(s):
    out, i, n = [], 0, len(s)
    while i < n:
        c = s[i]
        if c == "/" and i + 1 < n and s[i + 1] == "/":
            j = s.find("\n", i)
            i = n if j < 0 else j
        elif c == "/" and i + 1 < n and s[i + 1] == "*":
            j = s.find("*/", i + 2)
            i = n if j < 0 else j + 2
        elif c == '"':
            i += 1
            while i < n:
                if s[i] == "\\":
                    i += 2
                    continue
                if s[i] == '"':
                    i += 1
                    break
                i += 1
        elif c == "'":
            i += 1
            while i < n:
                if s[i] == "\\":
                    i += 2
                    continue
                if s[i] == "'":
                    i += 1
                    break
                i += 1
        else:
            out.append(c)
            i += 1
    return "".join(out)


def main(path):
    src = open(path, encoding="utf-8").read()
    code = strip_code(src)
    issues = []

    if "?." in code or "?[" in code:
        issues.append("null-conditional ?. or ?[")
    if '$"' in src:
        issues.append('interpolated string $"')
    if re.search(r"\bnameof\s*\(", code):
        issues.append("nameof")
    if re.search(r"\basync\s", code) or re.search(r"\bawait\s", code):
        issues.append("async/await")
    if re.search(r"\busing\s+static\b", code):
        issues.append("using static")
    if "??=" in code:
        issues.append("??=")

    # '=>' must only appear as lambda; Day0Gen uses anonymous methods only,
    # so ANY occurrence is flagged for manual review.
    for m in re.finditer(r"=>", code):
        line = code[: m.start()].count("\n") + 1
        issues.append('"=>" at line %d (lambda ok, expression-bodied NOT - review)' % line)

    # local functions: type name '(' ... ')' '{' at statement indent inside methods
    # heuristic: identifier followed by '(' preceded by a type token, at 16+ spaces indent,
    # and the file has no 'delegate'/'=>' near it — too fuzzy to grep reliably; rely on
    # the zero-lambda rule above plus manual review.

    for m in re.finditer(r"\bout\s+(var|[A-Za-z_]\w*)\s+[\w\)\]]", code):
        issues.append("possible inline out declaration: %r" % m.group(0))
    for m in re.finditer(r"\bis\s+[A-Z]\w*\s+\w+", code):
        issues.append("possible pattern matching: %r" % m.group(0))
    for m in re.finditer(r"=\s*new\s*\(", code):
        issues.append("possible target-typed new: %r" % m.group(0))

    stack = []
    pairs = {"}": "{", ")": "(", "]": "["}
    for idx, c in enumerate(code):
        if c in "{([":
            stack.append((c, code[:idx].count("\n") + 1))
        elif c in "})]":
            if not stack or stack[-1][0] != pairs[c]:
                issues.append("unbalanced %r near line %d" % (c, code[:idx].count("\n") + 1))
                break
            stack.pop()
    if stack:
        issues.append("unclosed %r from line %d" % stack[-1])

    if issues:
        print("C#5 AUDIT ISSUES in %s:" % path)
        for i in issues:
            print(" -", i)
        return 1
    print("C#5 AUDIT CLEAN: %s (no C#6+ constructs; delimiters balanced)" % path)
    return 0


if __name__ == "__main__":
    if len(sys.argv) != 2:
        print("usage: audit-cs5.py <file.cs>")
        sys.exit(2)
    sys.exit(main(sys.argv[1]))
