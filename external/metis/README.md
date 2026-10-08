METIS 5.2.1 and GKlib sources match the Nanite builder dependency in Epic
UE 5.8.3, commit `396c9f059903aed5fec78ecd3d437a40c6415368`.

The importer builds this required native dependency with CMake and copies
the shared library into cook tool outputs. METIS uses 32-bit indices and
32-bit real values. Upstream licenses are included with both source trees.
