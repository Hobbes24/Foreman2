internal class RecipeNodeSnapshot
{
    public RecipeQualityPair BaseRecipe { get; }
    public AssemblerQualityPair SelectedAssembler { get; }
    public List<ModuleQualityPair> AssemblerModules { get; }
    public Item Fuel { get; }
    public double NeighbourCount { get; }
    public double ExtraProductivityBonus { get; }
    public BeaconQualityPair SelectedBeacon { get; }
    public List<ModuleQualityPair> BeaconModules { get; }
    public double BeaconCount { get; }
    public double BeaconsPerAssembler { get; }
    public double BeaconsConst { get; }
    public bool LowPriority { get; }
    public bool KeyNode { get; }
    public string KeyNodeTitle { get; }
    public Point Location { get; }
    public NodeDirection NodeDirection { get; }

    // Each entry is (supplier node, item) for inputs, (consumer node, item) for outputs
    public List<(ReadOnlyBaseNode Node, ItemQualityPair Item)> InputLinks { get; }
    public List<(ReadOnlyBaseNode Node, ItemQualityPair Item)> OutputLinks { get; }

    public RecipeNodeSnapshot(ReadOnlyRecipeNode node)
    {
        BaseRecipe = node.BaseRecipe;
        SelectedAssembler = node.SelectedAssembler;
        AssemblerModules = node.AssemblerModules.ToList();
        Fuel = node.Fuel;
        NeighbourCount = node.NeighbourCount;
        ExtraProductivityBonus = node.ExtraProductivity;
        SelectedBeacon = node.SelectedBeacon;
        BeaconModules = node.BeaconModules.ToList();
        BeaconCount = node.BeaconCount;
        BeaconsPerAssembler = node.BeaconsPerAssembler;
        BeaconsConst = node.BeaconsConst;
        LowPriority = node.LowPriority;
        KeyNode = node.KeyNode;
        KeyNodeTitle = node.KeyNodeTitle;
        Location = node.Location;
        NodeDirection = node.NodeDirection;

        InputLinks = node.InputLinks
            .Select(l => (l.Supplier, l.Item))
            .ToList();
        OutputLinks = node.OutputLinks
            .Select(l => (l.Consumer, l.Item))
            .ToList();
    }
}