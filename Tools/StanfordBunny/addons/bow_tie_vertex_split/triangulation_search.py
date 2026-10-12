"""Deterministic, bounded triangulations using only existing polygon corners.

This module deliberately has no Blender dependencies.  An empty result means
the polygon cannot safely be handled by this search; callers can then preserve
their existing fallback.  Larger polygons are sampled, not exhaustively solved.
"""

import math


MAX_VERTICES = 16
MAX_CANDIDATES = 4096
_LENGTH_EPSILON = 1.0e-12
_AREA_EPSILON = 1.0e-12


def _normalized_coords(coords):
    """Translate and scale first so geometric tolerances are scale relative."""
    try:
        points = [tuple(float(point[axis]) for axis in range(3))
                  for point in coords]
    except (TypeError, ValueError, IndexError, OverflowError):
        return None
    if not points or not all(math.isfinite(value)
                             for point in points for value in point):
        return None
    origin = points[0]
    shifted = [tuple(point[axis] - origin[axis] for axis in range(3))
               for point in points]
    scale = max(abs(value) for point in shifted for value in point)
    if not math.isfinite(scale) or scale == 0.0:
        return None
    return [tuple(value / scale for value in point) for point in shifted]


def _unit_normal(normal):
    try:
        vector = tuple(float(normal[axis]) for axis in range(3))
    except (TypeError, ValueError, IndexError, OverflowError):
        return None
    if not all(math.isfinite(value) for value in vector):
        return None
    scale = max(abs(value) for value in vector)
    if scale == 0.0:
        return None
    vector = tuple(value / scale for value in vector)
    length = math.sqrt(math.fsum(value * value for value in vector))
    return tuple(value / length for value in vector)


def _orient(a, b, c):
    return ((b[0] - a[0]) * (c[1] - a[1])
            - (b[1] - a[1]) * (c[0] - a[0]))


def _on_segment(point, a, b):
    return (abs(_orient(a, b, point)) <= _AREA_EPSILON
            and min(a[0], b[0]) - _LENGTH_EPSILON <= point[0]
            <= max(a[0], b[0]) + _LENGTH_EPSILON
            and min(a[1], b[1]) - _LENGTH_EPSILON <= point[1]
            <= max(a[1], b[1]) + _LENGTH_EPSILON)


def _segments_intersect(a, b, c, d):
    """Include touching/overlap: such a diagonal is not safe to introduce."""
    ab_c, ab_d = _orient(a, b, c), _orient(a, b, d)
    cd_a, cd_b = _orient(c, d, a), _orient(c, d, b)
    if ((ab_c > _AREA_EPSILON and ab_d < -_AREA_EPSILON
         or ab_c < -_AREA_EPSILON and ab_d > _AREA_EPSILON)
            and (cd_a > _AREA_EPSILON and cd_b < -_AREA_EPSILON
                 or cd_a < -_AREA_EPSILON and cd_b > _AREA_EPSILON)):
        return True
    return (_on_segment(c, a, b) or _on_segment(d, a, b)
            or _on_segment(a, c, d) or _on_segment(b, c, d))


def _strictly_inside(point, polygon):
    """Even/odd containment, excluding the polygon boundary."""
    inside = False
    for index, a in enumerate(polygon):
        b = polygon[(index + 1) % len(polygon)]
        if _on_segment(point, a, b):
            return False
        if (a[1] > point[1]) != (b[1] > point[1]):
            crossing = a[0] + ((point[1] - a[1]) / (b[1] - a[1])) * (b[0] - a[0])
            if crossing > point[0]:
                inside = not inside
    return inside


def _cross(a, b, c):
    ab = tuple(b[axis] - a[axis] for axis in range(3))
    ac = tuple(c[axis] - a[axis] for axis in range(3))
    return (ab[1] * ac[2] - ab[2] * ac[1],
            ab[2] * ac[0] - ab[0] * ac[2],
            ab[0] * ac[1] - ab[1] * ac[0])


def _combine_candidates(left, right, triangle):
    """Visit both child lists early, without materializing their product."""
    for total in range(len(left) + len(right) - 1):
        first = max(0, total - len(right) + 1)
        last = min(total, len(left) - 1)
        for left_index in range(first, last + 1):
            yield tuple(sorted(left[left_index] + right[total - left_index]
                               + (triangle,)))


def valid_triangulations(coords, normal, max_candidates=256):
    """Return at most ``max_candidates`` safe local-index triangulations.

    Each result is a tuple of ``n - 2`` triangles, with corner order matching
    the input polygon.  A stable dominant-normal-axis projection must be simple,
    have distinct corners and nonzero area, and agree with ``normal``.  Every
    internal diagonal must stay inside that projection; every triangle must be
    nondegenerate in 3D and point in the supplied normal's direction.

    Dynamic-programming intervals retain bounded candidate lists.  With the
    default bound, the search is complete through eight corners (14 possible
    splits of a convex hexagon and 132 of a convex octagon).  For nine through
    sixteen corners the deterministic sample may miss an otherwise usable
    split.  The request is capped at 4096 candidates per interval.  Unsupported
    corner counts, invalid inputs, and unsafe projections return ``[]``.
    """
    try:
        limit = min(int(max_candidates), MAX_CANDIDATES)
    except (TypeError, ValueError, OverflowError):
        return []
    if limit <= 0:
        return []
    points = _normalized_coords(coords)
    unit_normal = _unit_normal(normal)
    if points is None or unit_normal is None or not 3 <= len(points) <= MAX_VERTICES:
        return []
    count = len(points)
    # Cyclic coordinate pairs make positive projected area point along the
    # positive omitted axis.  max() breaks equal-component ties by axis order.
    axis = max(range(3), key=lambda item: abs(unit_normal[item]))
    u, v = (axis + 1) % 3, (axis + 2) % 3
    polygon = [(point[u], point[v]) for point in points]
    for index, point in enumerate(polygon):
        for other in polygon[index + 1:]:
            if math.fsum((point[item] - other[item]) ** 2 for item in range(2)) <= _LENGTH_EPSILON ** 2:
                return []
    signed_area = math.fsum(
        polygon[index][0] * polygon[(index + 1) % count][1]
        - polygon[(index + 1) % count][0] * polygon[index][1]
        for index in range(count))
    if abs(signed_area) <= _AREA_EPSILON or signed_area * unit_normal[axis] <= 0.0:
        return []
    winding = 1.0 if signed_area > 0.0 else -1.0
    for first in range(count):
        next_first = (first + 1) % count
        for second in range(first + 1, count):
            next_second = (second + 1) % count
            if second == next_first or next_second == first:
                continue
            if _segments_intersect(polygon[first], polygon[next_first],
                                   polygon[second], polygon[next_second]):
                return []

    allowed = [[False] * count for _ in range(count)]
    for first in range(count):
        for second in range(first + 1, count):
            if second == first + 1 or (first == 0 and second == count - 1):
                allowed[first][second] = allowed[second][first] = True
                continue
            a, b = polygon[first], polygon[second]
            # A diagonal cannot run through another source corner, including
            # a corner on one of its incident boundary edges.
            if any(_on_segment(point, a, b) for index, point in enumerate(polygon)
                   if index not in (first, second)):
                continue
            if any(_segments_intersect(a, b, polygon[index], polygon[(index + 1) % count])
                   for index in range(count)
                   if first not in (index, (index + 1) % count)
                   and second not in (index, (index + 1) % count)):
                continue
            midpoint = ((a[0] + b[0]) * 0.5, (a[1] + b[1]) * 0.5)
            if _strictly_inside(midpoint, polygon):
                allowed[first][second] = allowed[second][first] = True

    candidates = {(index, index + 1): [()] for index in range(count - 1)}
    for span in range(2, count):
        for first in range(count - span):
            last = first + span
            results = []
            candidates[first, last] = results
            if not allowed[first][last]:
                continue
            branches = []
            for middle in range(first + 1, last):
                if not allowed[first][middle] or not allowed[middle][last]:
                    continue
                if winding * _orient(polygon[first], polygon[middle], polygon[last]) <= _AREA_EPSILON:
                    continue
                cross = _cross(points[first], points[middle], points[last])
                if (math.fsum(value * value for value in cross) <= _AREA_EPSILON ** 2
                        or math.fsum(cross[item] * unit_normal[item] for item in range(3)) <= _AREA_EPSILON):
                    continue
                left, right = candidates[first, middle], candidates[middle, last]
                if left and right:
                    branches.append(_combine_candidates(left, right, (first, middle, last)))
            # Distribute the budget across root splits.  Filling it from the
            # first split alone would force one particular ear into every
            # result on large polygons and miss simple collision alternatives.
            while branches and len(results) < limit:
                remaining = []
                for branch in branches:
                    result = next(branch, None)
                    if result is not None:
                        results.append(result)
                        remaining.append(branch)
                        if len(results) >= limit:
                            break
                branches = remaining
    return candidates[0, count - 1]


def triangle_quality(coords, triangles):
    """Return mean 3D aspect penalty (1 for equilateral); lower is better.

    This scale-independent scalar is a geometric tie-break only.  Invalid or
    degenerate triangles return infinity.  Candidate ordering itself is stable,
    so callers can retain input order when two penalties are exactly equal.
    """
    points = _normalized_coords(coords)
    if points is None:
        return math.inf
    penalties = []
    try:
        for triangle in triangles:
            if len(triangle) != 3 or any(index < 0 or index >= len(points) for index in triangle):
                return math.inf
            a, b, c = (points[index] for index in triangle)
            cross = _cross(a, b, c)
            doubled_area = math.sqrt(math.fsum(value * value for value in cross))
            if doubled_area <= _AREA_EPSILON:
                return math.inf
            squared_edges = math.fsum((left[axis] - right[axis]) ** 2
                                     for left, right in ((a, b), (b, c), (c, a))
                                     for axis in range(3))
            penalties.append(squared_edges / (2.0 * math.sqrt(3.0) * doubled_area))
    except (TypeError, ValueError, IndexError, OverflowError):
        return math.inf
    return math.fsum(penalties) / len(penalties) if penalties else math.inf
