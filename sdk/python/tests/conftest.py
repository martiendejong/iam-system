import sys
from pathlib import Path

# Make `iam_sdk` importable when running pytest directly from sdk/python
# without an editable install.
sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
