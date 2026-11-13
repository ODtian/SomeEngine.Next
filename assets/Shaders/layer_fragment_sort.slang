#ifndef LAYER_FRAGMENT_SORT_SLANG
#define LAYER_FRAGMENT_SORT_SLANG

__exported import layer_fragment_types;

struct LayerSortRecord
{
    LayerFragment Fragment;
    uint PayloadIndex;
};

bool LayerRecordBefore(LayerSortRecord left, LayerSortRecord right, uint sortMode)
{
    if (sortMode == 1u)
    {
        uint a = LayerFragmentDrawOrder(left.Fragment);
        uint b = LayerFragmentDrawOrder(right.Fragment);
        if (a != b) return a < b;
    }
    if (left.Fragment.Depth != right.Fragment.Depth)
        return sortMode == 2u ? left.Fragment.Depth > right.Fragment.Depth : left.Fragment.Depth < right.Fragment.Depth;
    return left.Fragment.Identity < right.Fragment.Identity;
}

#endif
