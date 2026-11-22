#!/usr/bin/env bash
# Usage: compare-trees.sh <dirA> <dirB>. Compares relative path + SHA-256 of every file.
set -euo pipefail
list() { (cd "$1" && find . -type f -print0 | LC_ALL=C sort -z | xargs -0 sha256sum); }
list "$1" > /tmp/treeA.txt
list "$2" > /tmp/treeB.txt
echo "A: $(wc -l < /tmp/treeA.txt) files  ($1)"
echo "B: $(wc -l < /tmp/treeB.txt) files  ($2)"
if diff /tmp/treeA.txt /tmp/treeB.txt > /tmp/treeDiff.txt; then
  echo "IDENTICAL: same relative paths and same SHA-256 for every file"
else
  echo "DIFFERENT:"; cat /tmp/treeDiff.txt | head -40; exit 1
fi
