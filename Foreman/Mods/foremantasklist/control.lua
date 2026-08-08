-- control.lua
-- Foreman2 Task List mod

local FRAME_NAME         = "foreman_tasklist_frame"
local DOUBLE_CLICK_TICKS = 30  -- ~0.5 seconds at 60 UPS

-- ============================================================
-- Storage init
-- ============================================================

script.on_init(function()
    storage.tasks        = {}   -- [player_index] = { {display, internal_name, count, done}, ... }
    storage.last_click   = {}   -- [player_index] = { name, tick }
    storage.search_tags  = {}   -- [player_index] = { position, ... }
    storage.item_cache   = {}   -- [internal_name] = item_name  (or false if none found)
end)

script.on_configuration_changed(function()
    storage.tasks       = storage.tasks       or {}
    storage.last_click  = storage.last_click  or {}
    storage.search_tags = storage.search_tags or {}
    storage.item_cache  = storage.item_cache  or {}
end)

-- ============================================================
-- Toggle UI on keybind  (Ctrl+B)
-- ============================================================

script.on_event("foreman-tasklist-toggle", function(event)
    local player = game.players[event.player_index]
    local frame  = player.gui.screen[FRAME_NAME]
    if frame then
        frame.destroy()
    else
        build_gui(player)
    end
end)

-- ============================================================
-- Inventory helpers
-- ============================================================

-- Resolves an entity internal name to the item name used in inventory.
-- Tries direct match first; falls back to scanning item prototypes for
-- one whose place_result matches the entity. Result is cached in storage.
function resolve_item_name(internal_name)
    -- Lazy-init: handles saves that pre-date this storage key
    if not storage.item_cache then storage.item_cache = {} end
    local cached = storage.item_cache[internal_name]
    if cached ~= nil then
        -- false means "no item found"; nil means "not yet looked up"
        return cached or nil
    end

    -- Direct match (the common case — entity name == item name)
    if prototypes.item[internal_name] then
        storage.item_cache[internal_name] = internal_name
        return internal_name
    end

    -- Fallback: scan all items for one that places this entity
    for item_name, item_proto in pairs(prototypes.item) do
        if item_proto.place_result and item_proto.place_result.name == internal_name then
            storage.item_cache[internal_name] = item_name
            return item_name
        end
    end

    -- Nothing found — cache the miss so we don't scan again
    storage.item_cache[internal_name] = false
    return nil
end

-- Returns how many of this entity's corresponding item the player is carrying.
function get_inventory_count(player, internal_name)
    if not storage.tasks       then storage.tasks       = {} end
    if not storage.search_tags then storage.search_tags = {} end
    local item_name = resolve_item_name(internal_name)
    if not item_name then return 0 end
    local inv = player.get_main_inventory()
    if not inv then return 0 end
    return inv.get_item_count(item_name)
end

-- Walks all tasks for a player; if they have >= needed and the task isn't
-- already done, marks it done. Never un-checks a task (one-way).
function auto_check_tasks(player)
    local tasks = storage.tasks[player.index]
    if not tasks then return end
    for _, task in ipairs(tasks) do
        if not task.done then
            local have = get_inventory_count(player, task.internal_name)
            if have >= task.count then
                task.done = true
            end
        end
    end
end

-- ============================================================
-- Build the GUI
-- ============================================================

function build_gui(player)
    local frame = player.gui.screen.add{
        type      = "frame",
        name      = FRAME_NAME,
        direction = "vertical",
        caption   = "Foreman2 Task List"
    }
    frame.auto_center = true

    -- Scrollable task list
    local scroll = frame.add{
        type      = "scroll-pane",
        name      = "task_scroll",
        direction = "vertical"
    }
    scroll.style.maximal_height = 400
    scroll.style.minimal_width  = 400
    scroll.style.padding        = 4

    render_tasks(player, scroll)

    frame.add{ type = "line" }

    -- Paste area
    local paste_section = frame.add{
        type      = "flow",
        name      = "paste_section",
        direction = "vertical"
    }
    paste_section.add{
        type    = "label",
        caption = "Paste Foreman2 export here, then click Import:"
    }
    local textbox = paste_section.add{
        type = "text-box",
        name = "paste_box"
    }
    textbox.style.width  = 380
    textbox.style.height = 80

    -- Hide paste area if tasks already exist
    local tasks = storage.tasks[player.index]
    if tasks and #tasks > 0 then
        paste_section.visible = false
    end

    frame.add{ type = "line" }

    -- Button row
    local btn_row = frame.add{
        type      = "flow",
        name      = "button_row",
        direction = "horizontal"
    }
    btn_row.style.horizontal_spacing = 6

    btn_row.add{
        type    = "button",
        name    = "foreman_import_btn",
        caption = (tasks and #tasks > 0) and "Import More" or "Import"
    }
    btn_row.add{
        type    = "button",
        name    = "foreman_clear_btn",
        caption = "Clear All"
    }
    btn_row.add{
        type    = "button",
        name    = "foreman_clearpins_btn",
        caption = "Clear Pins"
    }
end

-- ============================================================
-- Render task list into the scroll pane
-- ============================================================

function render_tasks(player, scroll)
    scroll.clear()

    local tasks = storage.tasks[player.index]
    if not tasks or #tasks == 0 then
        scroll.add{
            type    = "label",
            caption = "[color=150,150,150]No tasks. Paste a Foreman2 list and click Import.[/color]"
        }
        return
    end

    for i, task in ipairs(tasks) do
        local have = get_inventory_count(player, task.internal_name)
        local met  = (have >= task.count)

        -- Label format:
        --   Incomplete, not met:  "12x Assembling Machine 3 [asm-3]  [color=orange](5)[/color]"
        --   Incomplete, met:      "12x Assembling Machine 3 [asm-3]  [color=green](20)[/color]"
        --   Done (any):           "[color=grey]12x Assembling Machine 3 [asm-3][/color]  [color=green](20)[/color]"
        local base      = task.display .. " [" .. task.internal_name .. "]"
        local count_col = met and "0,200,0" or "200,150,0"
        local count_tag = "  [color=" .. count_col .. "](" .. have .. ")[/color]"
        local caption
        if task.done then
            caption = "[color=100,100,100]" .. base .. "[/color]" .. count_tag
        else
            caption = base .. count_tag
        end

        local row = scroll.add{
            type      = "flow",
            name      = "task_row_" .. i,
            direction = "horizontal"
        }
        row.style.vertical_align = "center"

        row.add{
            type    = "checkbox",
            name    = "task_check_" .. i,
            state   = task.done,
            caption = ""
        }

        local lbl = row.add{
            type    = "label",
            name    = "task_label_" .. i,
            caption = caption
        }
        lbl.style.left_margin = 4
    end
end

-- ============================================================
-- Refresh (redraw) the task list in an open GUI
-- ============================================================

function refresh_gui(player)
    local frame = player.gui.screen[FRAME_NAME]
    if not frame then return end
    local scroll = frame.task_scroll
    if scroll then
        render_tasks(player, scroll)
    end
end

-- ============================================================
-- Inventory change event — auto-check and refresh counts
-- ============================================================

script.on_event(defines.events.on_player_main_inventory_changed, function(event)
    local player = game.players[event.player_index]
    auto_check_tasks(player)
    -- Always refresh so counts stay current while GUI is open
    refresh_gui(player)
end)

-- ============================================================
-- Parse pasted Foreman2 export into tasks
-- Format expected:  "12x Assembling Machine 3 [assembling-machine-3]"
-- ============================================================

function parse_and_store_tasks(player, text)
    local tasks = storage.tasks[player.index] or {}
    storage.tasks[player.index] = tasks

    for line in text:gmatch("[^\r\n]+") do
        line = line:match("^%s*(.-)%s*$")   -- trim whitespace
        if line ~= "" then
            local count_str, display_name, internal_name =
                line:match("^(%d+)x%s+(.-)%s+%[([^%]]+)%]$")

            if count_str and display_name and internal_name then
                local needed = tonumber(count_str)
                local have = get_inventory_count(player, internal_name)
                table.insert(tasks, {
                    display       = count_str .. "x " .. display_name,
                    name          = display_name,
                    internal_name = internal_name,
                    count         = needed,
                    done          = (have >= needed)
                })
            end
        end
    end
end

-- ============================================================
-- Map pin search
-- ============================================================

local CRAFTING_TYPES = {
    "assembling-machine", "furnace", "rocket-silo", "agricultural-tower"
}

-- Find all machines on this surface whose active recipe produces item_name
function find_producers_for_item(player, item_name)
    local results = {}
    for _, etype in ipairs(CRAFTING_TYPES) do
        for _, machine in ipairs(player.surface.find_entities_filtered{ type = etype }) do
            local ok, recipe = pcall(machine.get_recipe, machine)
            if ok and recipe then
                for _, product in ipairs(recipe.products) do
                    if product.name == item_name then
                        table.insert(results, machine)
                        break
                    end
                end
            end
        end
    end
    return results
end

function search_for_producers(player, internal_name, display_name)
    clear_search_tags(player)

    -- internal_name == the item name (entity and item share the same name for placeables).
    -- Search for assemblers whose recipe produces this item.
    local search_item = internal_name
    local found = find_producers_for_item(player, search_item)

    -- Pyanodon's '-turd' suffix exists on the entity name but NOT on the item name.
    -- e.g. entity "fish-farm-mk01-turd", item "fish-farm-mk01"
    -- Strip the suffix and retry the item search.
    if #found == 0 and internal_name:sub(-5) == "-turd" then
        search_item = internal_name:sub(1, -6)
        found = find_producers_for_item(player, search_item)
    end

    if #found == 0 then
        player.print("[Foreman2] No assemblers producing '" .. display_name .. "' found on this surface.")
        return
    end

    -- Place chart tag pins at every producing machine
    storage.search_tags[player.index] = {}
    local icon_item = resolve_item_name(search_item) or "iron-gear-wheel"
    for _, machine in ipairs(found) do
        local tag = player.force.add_chart_tag(player.surface, {
            position = machine.position,
            text     = "[F2] " .. display_name,
            icon     = { type = "item", name = icon_item }
        })
        if tag then
            table.insert(storage.search_tags[player.index], tag.position)
        end
    end

    player.set_controller{
        type     = defines.controllers.remote,
        position = found[1].position,
        surface  = player.surface
    }
    player.zoom = 0.3

    player.print("[Foreman2] Found " .. #found .. " assembler(s) producing " .. display_name .. ". Click 'Clear Pins' to remove.")
end

function clear_search_tags(player)
    local tags = storage.search_tags[player.index]
    if tags then
        for _, pos in ipairs(tags) do
            local nearby = player.force.find_chart_tags(player.surface, {
                left_top     = { x = pos.x - 0.5, y = pos.y - 0.5 },
                right_bottom = { x = pos.x + 0.5, y = pos.y + 0.5 }
            })
            for _, tag in ipairs(nearby) do
                if tag.text:sub(1, 5) == "[F2] " then
                    tag.destroy()
                end
            end
        end
    end
    storage.search_tags[player.index] = {}
end

-- ============================================================
-- GUI event handler
-- ============================================================

script.on_event(defines.events.on_gui_click, function(event)
    local player  = game.players[event.player_index]
    local element = event.element
    if not element or not element.valid then return end

    local name = element.name

    -- Import button
    if name == "foreman_import_btn" then
        local frame = player.gui.screen[FRAME_NAME]
        if not frame then return end
        local paste_section = frame.paste_section
        if not paste_section then return end

        if paste_section.visible then
            local textbox = paste_section.paste_box
            if textbox and textbox.text ~= "" then
                parse_and_store_tasks(player, textbox.text)
                textbox.text = ""
            end
            local tasks = storage.tasks[player.index]
            if tasks and #tasks > 0 then
                paste_section.visible = false
                frame.button_row.foreman_import_btn.caption = "Import More"
            end
            refresh_gui(player)
        else
            paste_section.visible = true
            frame.button_row.foreman_import_btn.caption = "Import"
        end
        return
    end

    -- Clear All button
    if name == "foreman_clear_btn" then
        storage.tasks[player.index] = {}
        local frame = player.gui.screen[FRAME_NAME]
        if frame then
            frame.paste_section.visible = true
            frame.button_row.foreman_import_btn.caption = "Import"
        end
        refresh_gui(player)
        return
    end

    -- Clear Pins button
    if name == "foreman_clearpins_btn" then
        clear_search_tags(player)
        player.print("[Foreman2] Map pins cleared.")
        return
    end

    -- Checkbox toggle — manual check/uncheck
    local check_idx = name:match("^task_check_(%d+)$")
    if check_idx then
        local idx   = tonumber(check_idx)
        local tasks = storage.tasks[player.index]
        if tasks and tasks[idx] then
            tasks[idx].done = element.state
            refresh_gui(player)
        end
        return
    end

    -- Task label — single vs double click for map search
    local label_idx = name:match("^task_label_(%d+)$")
    if label_idx then
        local idx   = tonumber(label_idx)
        local tasks = storage.tasks[player.index]
        if not tasks or not tasks[idx] then return end

        local task = tasks[idx]
        local lc   = storage.last_click[player.index]
        local tick = event.tick or game.tick

        if lc and lc.name == name and (tick - lc.tick) <= DOUBLE_CLICK_TICKS then
            storage.last_click[player.index] = nil
            search_for_producers(player, task.internal_name, task.name or task.display)
        else
            storage.last_click[player.index] = { name = name, tick = tick }
        end
        return
    end
end)

-- ============================================================
-- Keyboard checkbox toggle (in case user tabs to checkbox)
-- ============================================================

script.on_event(defines.events.on_gui_checked_state_changed, function(event)
    local player  = game.players[event.player_index]
    local element = event.element
    if not element or not element.valid then return end

    local check_idx = element.name:match("^task_check_(%d+)$")
    if check_idx then
        local idx   = tonumber(check_idx)
        local tasks = storage.tasks[player.index]
        if tasks and tasks[idx] then
            tasks[idx].done = element.state
            refresh_gui(player)
        end
    end
end)