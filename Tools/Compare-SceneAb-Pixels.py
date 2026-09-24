"""Read-only pixel comparison; never transforms or rewrites input captures."""
import json
import sys
import numpy as np
from PIL import Image

a = np.asarray(Image.open(sys.argv[1]).convert("RGB"), dtype=np.int16)
b = np.asarray(Image.open(sys.argv[2]).convert("RGB"), dtype=np.int16)
if a.shape != b.shape:
    raise ValueError("Capture sizes differ")
delta = np.abs(a - b)
print(json.dumps({"width": a.shape[1], "height": a.shape[0],
                  "changedPixels": int(np.count_nonzero(np.any(delta != 0, axis=2))),
                  "maxRgb": int(delta.max()), "meanAbsoluteRgb": float(delta.mean())}))
