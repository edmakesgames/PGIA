using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Assertions;
using UnityEngine.UIElements;

namespace PGIA
{
    /// <summary>
    /// To be shared between ModelViews, this represents the ircon of an item as it is being dragged around.
    /// 
    /// TODO:
    ///     -problem when overlapping odd-numbered mutli-cell items, tends to drift too far
    ///     -drag icon size getting fucked up during swap event
    ///     -stacking
    ///     -stack splitting
    ///     
    /// </summary>
    [CreateAssetMenu(fileName = "Drag Cursor", menuName = "PGIA/Drag Cursor")]
    public class DragCursor : ScriptableObject
    {
        #region Public Fields and Properties
        [Tooltip("A UI document describing the cursor.")]
        public VisualTreeAsset CursorAsset;

        [Tooltip("If the pointer is pressed down and doesn't move beyond this threshold the the drag will be 'sticky' upon release. I.E. it will not immeditately be dropped but will instead require another press.")]
        public float StickyDragMoveThreshold = 12;
        #endregion


        #region Private Fields - Drag State Info
        GridCellView SourceCellView;
        RectInt? SrcRegion;
        IGridItemModel Item;
        VisualElement CursorScreen;
        VisualElement CursorRoot;
        public bool IsDragging => Item != null;
        bool Initialized = false;
        Vector2 DragStartWorld;
        bool CandidateForStickyDrag = false;
        List<GridCellView> LastHoveredCells;
        readonly static List<GridCellView> TempList1 = new();
        readonly static List<GridCellView> TempList2 = new();
        #endregion


        #region UNITY_EDITOR
#if UNITY_EDITOR
        /// <summary>
        /// We need this bit of editor-only logic so that we can reset the Initialized state in the editor
        /// since SO assets referenced in a scene will come into existance at asignment and persist until
        /// domain reload.
        /// </summary>
        private void OnEnable()
        {
            Initialized = false;
            EditorApplication.playModeStateChanged += HandlePlayModeChanged;
        }

        private void OnDisable()
        {
            EditorApplication.playModeStateChanged -= HandlePlayModeChanged;
        }

        void HandlePlayModeChanged(PlayModeStateChange state)
        {
            Initialized = false;
        }
#endif
        #endregion


        #region Public Methods
        /// <summary>
        /// Currently, this is called by GridViewModel upon it's own initialization.
        /// If it has already been called once by any source, further calls will do nothing.
        /// </summary>
        public void Initialize(VisualElement cursorScreenRoot)
        {
            Assert.IsNotNull(cursorScreenRoot, "You must specify a valid UI Document root for the cursor container.");
            if (Initialized) return;
            Item = null;
            Initialized = true;
            CursorScreen = cursorScreenRoot;
            CursorRoot = CursorAsset.Instantiate();
            CursorScreen.Add(CursorRoot);

            CursorRoot.SetEnabled(true);
            CursorRoot.pickingMode = PickingMode.Ignore; //this seems to be bugged. setting it via the stylesheets in the ui builder works though
            CursorRoot.style.position = Position.Absolute;
            CursorRoot.style.flexShrink = 0;
            CursorRoot.style.flexGrow = 0;
            CursorRoot.style.visibility = Visibility.Hidden;
            CursorRoot.style.backgroundSize = new StyleBackgroundSize(new BackgroundSize(BackgroundSizeType.Contain));
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="view"></param>
        /// <param name="srouceModel"></param>
        /// <param name="item"></param>
        public void BeginDrag(PointerDownEvent evt, GridCellView clickedCellView)
        {
            //NOTE: It is vital to remember that when an item is multi-celled, the root cell (i.e. the top-left cell)
            //is stretched over all others and thus it is the only one that can be interacted with so it will always
            //be the cell passed to this function as 'clickedCellView'.

            if (IsDragging) return;
            if (clickedCellView.Item == null) return;

            var loc = clickedCellView.GridView.GetLocation(clickedCellView.Item);
            Assert.IsTrue(loc != null);
            Item = clickedCellView.Item;
            SrcRegion = loc.Value;
            SourceCellView = clickedCellView;
            DragStartWorld = clickedCellView.CellUI.LocalToWorld(evt.localPosition);
            CandidateForStickyDrag = true;

            CursorScreen.RegisterCallback<PointerMoveEvent>(HandleMove);
            CursorRoot.style.visibility = Visibility.Visible;
            CursorRoot.style.backgroundImage = new StyleBackground(Item.Shared.Icon);
            CursorRoot.style.width = clickedCellView.CellUI.style.width;
            CursorRoot.style.height = clickedCellView.CellUI.style.height;

            SetCursorPosition(evt.position.x, evt.position.y);
            if (!clickedCellView.GridView.RemoveItem(clickedCellView.Item))
            {
                ResetInternalState();
                return;
            }
        }

        /// <summary>
        /// 
        /// </summary>
        public void Drop(PointerUpEvent evt, GridCellView clickedCellView)
        {
            if (!IsDragging) return; //just in case we click elsewhere without starting a drag and then release over a slot
            if (CandidateForStickyDrag)
            {
                TintLastHoveredCells(SourceCellView.GridView.Shared.DefaultColorBackground, SourceCellView.GridView.Shared.DefaultColorIcon);
                HilightForDragging(evt.localPosition, clickedCellView);
                CandidateForStickyDrag = false;
                return;
            }
            Assert.IsNotNull(Item);

            var clickedModel = clickedCellView.GridView.Model;
            var region = CalculateBestFitCells(evt.localPosition, clickedCellView, Item);
            var swapItem = clickedModel.CheckForSwappableItem(Item, region.x, region.y);
            if (swapItem != null)
            {
                //we need to find the root cell of the swap item so that we can cache the size of its icon BEFORE we move it
                var modelRegion = clickedModel.GetLocation(swapItem).Value;
                var rootCell = clickedCellView.GridView.GetCellViews(clickedModel.GridWidth, clickedModel.GetLocation(swapItem).Value)[0];
                var swapImageWidth = rootCell.CellUI.style.width;
                var swapImageHeight = rootCell.CellUI.style.height;

                //start the process of moving
                if(!clickedCellView.GridView.Model.Swap(swapItem, Item, region))
                {
                    Cancel();
                    return;
                }

                //update internal state to reflect the new drag item
                #region Swap version of BeginDrag()
                Item = swapItem;
                var loc = SourceCellView.GridView.Model.FindOpenSpace(Item.Size.x, Item.Size.y);
                if (loc == null)
                    Debug.LogWarning("During an item swap sequence ample space was lost to confirm a cancellation action. The item will be dropped " +
                        "from the inventory and issued with the 'OnDroppedItem' event if the drag action is cancelled in this state. " +
                        "\n\nThis is not a bug but rather a heads up about this safety feature.");
                else SrcRegion = loc.Value;
                SourceCellView = clickedCellView;
                DragStartWorld = clickedCellView.CellUI.LocalToWorld(evt.localPosition);
                CandidateForStickyDrag = true;
                CursorRoot.style.backgroundImage = new StyleBackground(Item.Shared.Icon);
                CursorRoot.style.width = swapImageWidth;
                CursorRoot.style.height = swapImageHeight;
                SetCursorPosition(evt.position.x, evt.position.y);
                #endregion

                return;
            }


            if (!clickedCellView.GridView.StoreItem(Item, region))
                Cancel();
            else ResetInternalState();
        }

        /// <summary>
        /// Cancels an active drag operation and returns the item to its original location.
        /// </summary>
        public void Cancel()
        {
            if (!IsDragging) return;

            //put the item back where it came from, if this fails, not much we can do about it at this point
            if (SrcRegion == null || !SourceCellView.GridView.StoreItem(Item, SrcRegion.Value))
            {
                var rect = SourceCellView.GridView.Model.FindOpenSpace(Item.Size.x, Item.Size.y);
                if (!rect.HasValue || !SourceCellView.GridView.StoreItem(Item, rect.Value))
                {
                    Debug.LogWarning("Due to the amount of item swapping, no valid location could be found for this cancelled operation. As a result the item " +
                        "will be expelled from the inventory entirely and the 'OnDroppedItem' event will be triggered. " +
                        "\n\nThis is not a bug but rather a heads up about this safety feature.");
                    var model = Item.Container ?? SourceCellView.GridView.Model;
                    model.DropItem(Item);
                }
            }
            ResetInternalState();
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="evt"></param>
        /// <param name="cellView"></param>
        public void PointerHoverEnter(PointerEnterEvent evt, GridCellView cellView)
        {
            if (!IsDragging)
            {
                LastHoveredCells = new List<GridCellView> { cellView.RootCellView ?? cellView };
                TintLastHoveredCells(cellView.GridView.Shared.HilightColorBackground, cellView.GridView.Shared.HilightColorIcon);
            }

        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="evt"></param>
        /// <param name="cellView"></param>
        public void PointerHoverExit(PointerLeaveEvent evt, GridCellView cellView)
        {
            if(!IsDragging)
                TintLastHoveredCells(cellView.GridView.Shared.DefaultColorBackground, cellView.GridView.Shared.DefaultColorIcon);
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="evt"></param>
        /// <param name="cellView"></param>
        public void CellPointerMoved(PointerMoveEvent evt, GridCellView cellView)
        {
            if (IsDragging)
            {
                HilightForDragging(evt.localPosition, cellView);
                if(CandidateForStickyDrag)
                {
                    if ((DragStartWorld - cellView.CellUI.LocalToWorld(evt.localPosition)).sqrMagnitude > StickyDragMoveThreshold)
                        CandidateForStickyDrag = false;
                }
            }
            
        }
        #endregion


        #region Private Methods
        /// <summary>
        /// Shared by several methods to reset global state data to a before-dragging condition.
        /// </summary>
        void ResetInternalState()
        {
            CursorScreen.UnregisterCallback<PointerMoveEvent>(HandleMove);
            TintLastHoveredCells(SourceCellView.GridView.Shared.DefaultColorBackground, SourceCellView.GridView.Shared.DefaultColorIcon);
            LastHoveredCells = null;
            CursorRoot.style.visibility = Visibility.Hidden;
            SourceCellView = null;
            SrcRegion = null;
            Item = null;
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="evt"></param>
        /// <param name="cellView"></param>
        void HilightForDragging(Vector2 localPosition, GridCellView cellView)
        {
            //clear out previous cells if any
            TintLastHoveredCells(cellView.GridView.Shared.DefaultColorBackground, cellView.GridView.Shared.DefaultColorIcon);

            var region = CalculateBestFitCells(localPosition, cellView, Item);
            Color bgColor;
            Color iconTint;
            if (cellView.GridView.Model.CanMoveItemToLocation(Item, region) || 
                cellView.GridView.CheckForSwappableItem(Item, region.x, region.y) != null)
            {
                bgColor = cellView.GridView.Shared.ValidColorBackground;
                iconTint = cellView.GridView.Shared.ValidColorIcon;
            }
            else
            {
                bgColor = cellView.GridView.Shared.InvalidColorBackground;
                iconTint = cellView.GridView.Shared.InvalidColorIcon;
            }

            LastHoveredCells = cellView.GridView.GetCellViews(cellView.GridView.Model.GridWidth, region);
            AdjustListForOverlappedCells(LastHoveredCells);
            TintLastHoveredCells(bgColor, iconTint);
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="backgroundColor"></param>
        void TintLastHoveredCells(Color backgroundColor, Color iconTint)
        {
            if (LastHoveredCells != null)
            {
                foreach (var cell in LastHoveredCells)
                {
                    cell.CellUI.style.backgroundColor = backgroundColor;
                    if(cell.CellUI.style.backgroundImage != null)
                    {
                        cell.CellUI.style.unityBackgroundImageTintColor = iconTint;
                    }
                }
            }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="evt"></param>
        void HandleMove(PointerMoveEvent evt)
        {
            SetCursorPosition(evt.position.x, evt.position.y);
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="x"></param>
        /// <param name="y"></param>
        void SetCursorPosition(float x, float y)
        {
            CursorRoot.style.left = new StyleLength(x - (CursorRoot.style.width.value.value * 0.5f));
            CursorRoot.style.top = new StyleLength(y - (CursorRoot.style.height.value.value * 0.5f));
        }
        #endregion

        
        #region Static Helper Methods
        /// <summary>
        /// Calculates the best fit location of cells based on the pointer location. This is used to
        /// provide a more natural feeling when selecting where items will drop to on the grid.
        /// </summary>
        /// <param name="localPos"></param>
        /// <param name="cellView"></param>
        /// <returns></returns>
        static RectInt CalculateBestFitCells(Vector2 localPos, GridCellView cellView, IGridItemModel draggedItem)
        {
            if (draggedItem == null)
                return new RectInt(cellView.X, cellView.Y, 1, 1);

            int offsetX = cellView.X;
            int offsetY = cellView.Y;
            int w = draggedItem.Size.x;
            int h = draggedItem.Size.y;

            int gridWidth = cellView.GridView.Model.GridWidth;
            int gridHeight = cellView.GridView.Model.GridHeight;
            Vector2 normalized = new(localPos.x / cellView.GridView.CellWidth, localPos.y / cellView.GridView.CellHeight);
            float halfWay = 0.5f;

            if (w > 1)
            {
                if ((w & 0x1) == 1) offsetX -= (int)(w / 2); //odd width
                else if (normalized.x < halfWay) offsetX -= (int)(w / 2); //even width
                else offsetX -= (int)(w / 2) - 1; //even width
            }
            if (h > 1)
            {
                if ((h & 0x1) == 1) offsetY -= (int)(h / 2); //odd height
                else if (normalized.y < halfWay) offsetY -= (int)(h / 2); //even height
                else offsetY -= (int)(h / 2) - 1; //even height
            }

            //limit the final location to the bounds of the grid
            offsetX = Mathf.Max(0, offsetX);
            if (offsetX + w >= gridWidth)
                offsetX = gridWidth - w;

            offsetY = Mathf.Max(0, offsetY);
            if (offsetY + h > gridHeight)
                offsetY = gridHeight - h;

            return new RectInt(offsetX, offsetY, w, h);
        }

        /// <summary>
        /// Given a list of cells, this will be sure to include any root cells of multi-celled items who's children are
        /// in the initial list. It will also remove those children so as not to cause duplicated effects. This method
        /// is primarily for figuring out which cells in a RectInt region actually need hilighting applied.
        /// </summary>
        /// <param name="cells"></param>
        static void AdjustListForOverlappedCells(List<GridCellView> cells)
        {
            foreach (var cell in cells)
            {
                if (cell.RootCellView != null && !TempList2.Contains(cell))
                {
                    TempList1.Add(cell.RootCellView);
                    TempList2.Add(cell);
                }
            }

            cells.AddRange(TempList1);
            foreach (var cell in TempList2)
                cells.Remove(cell);
            TempList1.Clear();
            TempList2.Clear();
        }
        #endregion

    }
}
