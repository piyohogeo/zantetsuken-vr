"""Exact interval DP over disk triangulations of an ordered 3D boundary.

No projection is used in the objective. Self-intersections are permitted.
The extended state (i,k,j) retains the root triangle for fold-angle costs.
"""
from math import acos, pi, sqrt, isfinite, fsum, hypot, dist
from mathutils import Vector
from mathutils.geometry import area_tri

METRICS = ('AREA', 'LENGTH', 'SHAPE', 'FOLD')
MAX_VERTICES = 256
MAX_FOLD_VERTICES = 80
# Below roughly one part per million, binary32 mesh coordinates no longer
# describe a useful triangle for collision/export purposes.  Require both an
# altitude relative to the whole boundary and an altitude relative to the
# triangle's longest edge, so tiny valid local detail and long slivers are
# distinguished.
MIN_NORMALIZED_ALTITUDE = 1e-6
MIN_TRIANGLE_SLENDERNESS = 1e-6


def triangle_is_near_degenerate(points, reference_scale=None):
    """Return True for zero-area or numerically unusable three-point faces."""
    points = [tuple(float(x) for x in point) for point in points]
    if len(points) != 3 or not all(isfinite(x) for point in points for x in point):
        return True
    edges = [dist(points[(index + 1) % 3], points[index]) for index in range(3)]
    longest = max(edges)
    scale = longest if reference_scale is None else float(reference_scale)
    if not longest or not scale or not isfinite(scale):
        return True
    u = tuple(points[1][axis] - points[0][axis] for axis in range(3))
    v = tuple(points[2][axis] - points[0][axis] for axis in range(3))
    cross = (u[1] * v[2] - u[2] * v[1],
             u[2] * v[0] - u[0] * v[2],
             u[0] * v[1] - u[1] * v[0])
    twice_area = hypot(*cross)
    normalized_altitude = twice_area / (longest * scale)
    slenderness = twice_area / (longest * longest)
    return (not isfinite(twice_area)
            or normalized_altitude <= MIN_NORMALIZED_ALTITUDE
            or slenderness <= MIN_TRIANGLE_SLENDERNESS)


def optimize_ring(points, selected=('AREA',), forbidden_edges=(), forbidden_faces=(), allow_coincident=False):
    selected = frozenset(selected)
    if not selected or not selected.issubset(METRICS):
        raise ValueError('Select at least one valid optimization metric')
    n = len(points)
    vertex_limit = MAX_FOLD_VERTICES if 'FOLD' in selected else MAX_VERTICES
    if not 3 <= n <= vertex_limit:
        suffix = ' when Internal Fold Angle is selected' if 'FOLD' in selected else ''
        raise ValueError(
            f'Optimization supports 3..{vertex_limit} vertices per loop{suffix}')
    # Read Blender's stored coordinates once, then use Python binary64 math.
    # Never normalize in mathutils.Vector (binary32): that can turn an exact
    # midpoint into an apparently non-collinear point.
    points = [tuple(float(x) for x in p) for p in points]
    if not all(isfinite(x) for p in points for x in p):
        raise ValueError('Non-finite coordinates')
    center = tuple(fsum(p[a] for p in points)/n for a in range(3))
    scale = max(dist(p,center) for p in points)
    if not scale:
        raise ValueError('Zero-size boundary')
    # Unit normalization makes scores invariant to translation and uniform scale.
    lengths = {(i,j):dist(points[j],points[i])/scale for i in range(n) for j in range(i+1,n)}
    blender_points = [Vector(p) for p in points]
    forbidden_edges = {tuple(sorted(e)) for e in forbidden_edges}
    forbidden_faces = {tuple(sorted(f)) for f in forbidden_faces}
    triangles, costs = {}, {}
    zero_area = set()
    near_degenerate_nonzero = set()
    collapsed_triangles = set()
    for i in range(n-2):
        for k in range(i+1,n-1):
            for j in range(k+1,n):
                key = (i,k,j)
                if key in forbidden_faces or any(e in forbidden_edges for e in ((i,k),(k,j),(i,j))):
                    continue
                u=tuple(points[k][a]-points[i][a] for a in range(3))
                v=tuple(points[j][a]-points[i][a] for a in range(3))
                cross=(u[1]*v[2]-u[2]*v[1],u[2]*v[0]-u[0]*v[2],u[0]*v[1]-u[1]*v[0])
                magnitude=hypot(*cross)
                area = (magnitude/scale)/scale/2
                collapsed = allow_coincident and len({points[i],points[k],points[j]}) == 2
                if not isfinite(area):
                    continue
                blender_area = area_tri(
                    blender_points[i],blender_points[k],blender_points[j])
                exact_zero = not collapsed and (not magnitude or blender_area <= 0)
                unstable_nonzero = (not collapsed and not exact_zero
                                    and triangle_is_near_degenerate(
                                        (points[i],points[k],points[j]),scale))
                if collapsed:
                    collapsed_triangles.add(key)
                elif exact_zero:
                    zero_area.add(key)
                elif unstable_nonzero:
                    near_degenerate_nonzero.add(key)
                # The primary lexicographic cost is the number of unstable
                # triangles. Undefined secondary shape/fold terms are neutral.
                shape = (0 if not magnitude else
                         (lengths[i,k]**2+lengths[k,j]**2+lengths[i,j]**2)
                         /(4*sqrt(3)*area)-1)
                triangles[key] = (area, max(0.0,shape),
                                  None if not magnitude else tuple(x/magnitude for x in cross))
                # Charge each internal diagonal once, from its parent triangle.
                diagonal = ((lengths[i,k] if k-i>1 else 0) + (lengths[k,j] if j-k>1 else 0))/max(1,n-3)
                costs[key] = ((area if 'AREA' in selected else 0)
                              + (diagonal if 'LENGTH' in selected else 0)
                              + (max(0.0,shape)/(n-2) if 'SHAPE' in selected else 0))

    def fold(a,b):
        x,y = triangles[a][2],triangles[b][2]
        if x is None or y is None:return 0.0
        dot = max(-1.0,min(1.0,sum(x[t]*y[t] for t in range(3))))
        return (acos(dot)/pi)**2/max(1,n-3)

    chosen = []
    if 'FOLD' not in selected:
        dp, split = {}, {}
        for i in range(n-1):dp[i,i+1]=(0,0,0.0)
        for width in range(2,n):
            for i in range(n-width):
                j=i+width; best=(n+1,n+1,float('inf'))
                for k in range(i+1,j):
                    key=(i,k,j)
                    if key not in costs or (i,k) not in dp or (k,j) not in dp:continue
                    left,right=dp[i,k],dp[k,j]
                    value=(int(key in zero_area)+left[0]+right[0],
                           int(key in near_degenerate_nonzero)+left[1]+right[1],
                           costs[key]+left[2]+right[2])
                    if value<best:best=value;split[i,j]=k
                dp[i,j]=best
        energy=dp[0,n-1]
        if not isfinite(energy[2]):raise ValueError('No disk triangulation satisfies topology constraints')
        stack=[(0,n-1)]
        while stack:
            i,j=stack.pop()
            if j-i<2:continue
            k=split[i,j];chosen.append((i,k,j));stack.extend(((i,k),(k,j)))
    else:
        states, children, roots = {}, {}, {}
        for width in range(2,n):
            for i in range(n-width):
                j=i+width; valid=[]
                for k in range(i+1,j):
                    key=(i,k,j)
                    if key not in costs:continue
                    total=(int(key in zero_area),
                           int(key in near_degenerate_nonzero),costs[key]);links=[]
                    for a,b in ((i,k),(k,j)):
                        if b-a==1:links.append(None);continue
                        candidates=roots.get((a,b),[])
                        best=(n+1,n+1,float('inf'));winner=None
                        for child in candidates:
                            value=(states[child][0],states[child][1],
                                   states[child][2]+fold(key,child))
                            if value<best:best=value;winner=child
                        total=(total[0]+best[0],total[1]+best[1],
                               total[2]+best[2]);links.append(winner)
                    if isfinite(total[2]):
                        states[key]=total;children[key]=links;valid.append(key)
                roots[i,j]=valid
        if not roots.get((0,n-1)):raise ValueError('No valid disk triangulation satisfies topology constraints')
        root=min(roots[0,n-1],key=lambda key:states[key]);energy=states[root]
        stack=[root]
        while stack:
            key=stack.pop();chosen.append(key);stack.extend(c for c in children[key] if c is not None)

    edges={}
    for tri in chosen:
        for a,b in ((tri[0],tri[1]),(tri[1],tri[2]),(tri[2],tri[0])):
            edges.setdefault(tuple(sorted((a,b))),[]).append(tri)
    internal={e:ts for e,ts in edges.items() if len(ts)==2}
    scores={'AREA':sum(triangles[t][0] for t in chosen),
            'LENGTH':sum(lengths[e] for e in internal)/max(1,n-3),
            'SHAPE':sum(triangles[t][1] for t in chosen)/(n-2),
            'FOLD':sum(fold(*ts) for ts in internal.values())}
    metric_energy=energy[2]
    if abs(metric_energy-sum(scores[m] for m in selected))>1e-7*max(1,abs(metric_energy)):
        raise RuntimeError('Optimizer score verification failed')
    return chosen, {'scores':scores,'objective':metric_energy,'selected':sorted(selected),'vertices':n,
                    'triangles':len(chosen),'scale':scale,
                    'collapsed_triangles':[t for t in chosen if t in collapsed_triangles],
                    'zero_area_triangles':[t for t in chosen if t in zero_area],
                    'near_degenerate_triangles':[t for t in chosen
                        if t in zero_area or t in near_degenerate_nonzero]}
