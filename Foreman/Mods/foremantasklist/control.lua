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
    storage.sprite_cache = {}   -- [player_index] = sprite path for the request button
end)

script.on_configuration_changed(function()
    storage.tasks       = storage.tasks       or {}
    storage.last_click  = storage.last_click  or {}
    storage.search_tags = storage.search_tags or {}
    storage.item_cache  = storage.item_cache  or {}
    storage.sprite_cache = storage.sprite_cache or {}

    -- A frame built by an older version keeps that version's layout and button
    -- states, so rebuild anything left open.
    for _, player in pairs(game.players) do
        if player.gui.screen[FRAME_NAME] then
            player.gui.screen[FRAME_NAME].destroy()
            build_gui(player)
        end
    end
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

-- ============================================================
-- Personal logistic requests
-- ============================================================

-- All of our requests live in one logistic section identified by a group name.
-- Groups are force-wide, so the player name keeps two players on the same force
-- from stepping on each other.
function request_group_name(player)
    return "Foreman2 " .. player.name
end

-- The player's actual character entity, regardless of which controller (remote,
-- god, etc.) they currently have active. Our own search/pin feature
-- (search_for_entity) switches the player to the remote controller, and
-- player.character alone was not enough to find the character through that -
-- get_associated_characters() is the API's documented way to do it.
function get_player_character(player)
    local character = player.character
    if character and character.valid then return character end

    local ok, associated = pcall(function() return player.get_associated_characters() end)
    if ok and associated then
        for _, candidate in pairs(associated) do
            if candidate and candidate.valid then return candidate end
        end
    end

    return nil
end

-- The character's requester point. get_requester_point() is the 2.1 spelling,
-- get_logistic_point() the 2.0 one; reading a member a LuaObject does not have
-- raises, so both lookups go through pcall.
--
-- Queried against the character entity, not the player: LuaControl methods act
-- on whatever the player currently controls, and remote view (see
-- get_player_character above) leaves nothing controlled. The character - and
-- its logistic point - still exists in the world the whole time, so go
-- straight to it.
-- Second return value is a diagnostic string for the failure case, surfaced by
-- request_task_item so we can tell "no character" apart from "no point" apart
-- from "point exists but pcall errored" without guessing.
function get_requester_point(player)
    local character = get_player_character(player)
    if not character then return nil, "no character (player.character and get_associated_characters both empty)" end

    local ok, point = pcall(function() return character.get_requester_point() end)
    if ok and point and point.valid then return point end
    local reason1 = ok and "get_requester_point() returned nil" or ("get_requester_point() errored: " .. tostring(point))

    ok, point = pcall(function()
        return character.get_logistic_point(defines.logistic_member_index.character_requester)
    end)
    if ok and point and point.valid then return point end
    local reason2 = ok and "get_logistic_point() returned nil" or ("get_logistic_point() errored: " .. tostring(point))

    return nil, reason1 .. "; " .. reason2
end

-- Whether a request we write will actually be acted on. This is not the same
-- question as whether the write succeeds: the requester point exists and takes
-- set_slot happily even when the feature is off, so a request can be stored and
-- then quietly never filled. Two gates decide it, and neither reports itself at
-- the point of writing:
--   force.character_logistic_requests - false until personal logistics is
--     researched. This is the one that bites on a fresh save.
--   point.enabled - the player's own on/off switch in the logistics window.
-- Only a definitive false counts. If a member cannot be read at all (version
-- drift between 2.0 and 2.1), assume enabled rather than refuse a request that
-- would have worked - a missing warning is a smaller failure than a blocked
-- feature. Returns a reason string when blocked, nil when good to go.
function requests_blocked_reason(player)
    local ok, enabled = pcall(function() return player.force.character_logistic_requests end)
    if ok and enabled == false then
        return "personal logistics has not been researched yet"
    end

    local point = get_requester_point(player)
    if point then
        ok, enabled = pcall(function() return point.enabled end)
        if ok and enabled == false then
            return "personal logistics is switched off in your logistics window"
        end
    end

    return nil
end

-- On/off state of the player's personal logistics, as three values: true and
-- false when we can read the point, nil when there is no point to read. nil is
-- the unresearched case - not "off", because there is nothing there to switch.
-- The button relies on that distinction to know when to disable itself rather
-- than offer a toggle that cannot work.
function logistics_enabled_state(player)
    local point = get_requester_point(player)
    if not point then return nil end

    local ok, enabled = pcall(function() return point.enabled end)
    if not ok then return nil end

    return enabled and true or false
end

-- Flips that switch. Returns the new state, or nil if it could not be changed -
-- same nil-means-unavailable contract as above.
function set_logistics_enabled(player, target)
    local point = get_requester_point(player)
    if not point then return nil end

    local ok = pcall(function() point.enabled = target end)
    if not ok then return nil end

    -- Read back rather than trusting the write: the point is the authority, and
    -- a silently ignored assignment should not report success.
    return logistics_enabled_state(player)
end

-- Our section on that point. Creates it when `create` is set, otherwise returns
-- nil if we have never made a request for this player. Second return value is
-- a diagnostic string, see get_requester_point above.
function get_request_section(player, create)
    local point, reason = get_requester_point(player)
    if not point then return nil, reason end

    local group = request_group_name(player)
    for i = 1, point.sections_count do
        local section = point.get_section(i)
        if section and section.valid and section.group == group then
            return section
        end
    end

    if not create then return nil, "no section yet for group '" .. group .. "'" end
    local ok, section = pcall(function() return point.add_section(group) end)
    if ok and section and section.valid then return section end
    return nil, "add_section() failed: " .. tostring(section)
end

-- Finds the slot in our section already requesting item_name, or nil.
function find_request_slot(section, item_name)
    for slot = 1, section.filters_count do
        local ok, filter = pcall(function() return section.get_slot(slot) end)
        if ok and filter and filter.value and filter.value.name == item_name then
            return slot, filter
        end
    end
    return nil
end

-- Adds a personal logistic request for the item behind a task. An existing
-- request is only ever raised, never lowered.
function request_task_item(player, internal_name, count)
    local item_name = resolve_item_name(internal_name)
    if not item_name then
        player.print("[Foreman2] No item found for '" .. internal_name .. "'.")
        return false
    end

    local section, reason = get_request_section(player, true)
    if not section then
        -- Confirmed in game: with personal logistics unresearched the requester
        -- point comes back nil, so this path - not the post-write warning below
        -- - is what a fresh save actually hits.
        --
        -- When we can name the cause, say only that: the API detail is noise to
        -- a player who just wants to know why the button did nothing. It is
        -- printed only in the unexplained case, where separating "no character"
        -- from "no point" is the whole value of the message.
        local blocked = requests_blocked_reason(player)
        if blocked then
            player.print("[Foreman2] Personal logistic requests are not available: " .. blocked .. ".")
        else
            player.print("[Foreman2] Personal logistic requests are not available: " .. (reason or "unknown reason"))
        end
        return false
    end
    if not section.is_manual then
        player.print("[Foreman2] Personal logistic requests are not available: section exists but is_manual = false.")
        return false
    end

    local slot, filter = find_request_slot(section, item_name)
    if slot then
        if (filter.min or 0) < count then
            section.set_slot(slot, { value = filter.value, min = count, max = filter.max })
        end
        return true
    end

    -- max stays nil so nothing gets auto-trashed once the request is filled
    section.set_slot(section.filters_count + 1, {
        value = { type = "item", name = item_name, quality = "normal", comparator = "=" },
        min   = count
    })
    return true
end

-- Drops our request for one item. Never touches the player's own sections.
function clear_task_request(player, internal_name)
    local item_name = resolve_item_name(internal_name)
    if not item_name then return end

    local section = get_request_section(player, false)
    if not section or not section.is_manual then return end

    local slot = find_request_slot(section, item_name)
    if slot then section.clear_slot(slot) end
end

-- Empties our section. Returns how many requests were removed.
function clear_all_task_requests(player)
    local section = get_request_section(player, false)
    if not section or not section.is_manual then return 0 end

    local cleared = 0
    for slot = section.filters_count, 1, -1 do
        local ok, filter = pcall(function() return section.get_slot(slot) end)
        if ok and filter and filter.value then
            section.clear_slot(slot)
            cleared = cleared + 1
        end
    end
    return cleared
end

-- 2.0 moved is_valid_sprite_path onto `helpers`; before that it hung off `game`,
-- and it has never been on LuaGui. Reading a member an object does not have raises,
-- so both spellings go through pcall and a miss just means "no sprite".
function sprite_path_is_valid(path)
    local ok, valid = pcall(function() return helpers.is_valid_sprite_path(path) end)
    if ok then return valid end

    ok, valid = pcall(function() return game.is_valid_sprite_path(path) end)
    if ok then return valid end

    return false
end

-- Icon for the per-row request button. Base's logistic robot is the obvious
-- choice but a modpack can remove it, so fall back until something resolves.
function request_button_sprite(player)
    if not storage.sprite_cache then storage.sprite_cache = {} end
    local cached = storage.sprite_cache[player.index]
    if cached ~= nil then return cached or nil end

    for _, path in ipairs({ "item/logistic-robot", "item/requester-chest", "utility/slot_icon_robot" }) do
        if sprite_path_is_valid(path) then
            storage.sprite_cache[player.index] = path
            return path
        end
    end

    storage.sprite_cache[player.index] = false
    return nil
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
                -- the request has been filled; stop the bots hauling more
                clear_task_request(player, task.internal_name)
            end
        end
    end
end

-- ============================================================
-- Button state
-- ============================================================

-- Import is the only button that does anything without a task list, so the rest
-- stay disabled until one has been imported.
local GATED_BUTTONS = {
    { row = "button_row",    name = "foreman_clear_btn",         tooltip = "Discard the imported list and its pins" },
    { row = "button_row",    name = "foreman_clearpins_btn",     tooltip = "Remove the map pins this mod placed" },
    { row = "logistics_row", name = "foreman_requestall_btn",    tooltip = "Add a personal logistic request for every unchecked task" },
    { row = "logistics_row", name = "foreman_clearrequests_btn", tooltip = "Remove the logistic requests this mod added" }
}

local NO_TASKS_TOOLTIP = "Import a Foreman2 list first"

function update_button_states(player)
    local frame = player.gui.screen[FRAME_NAME]
    if not frame then return end

    local tasks     = storage.tasks[player.index]
    local has_tasks = (tasks ~= nil and #tasks > 0)

    for _, spec in ipairs(GATED_BUTTONS) do
        local row    = frame[spec.row]
        local button = row and row[spec.name]
        if button then
            button.enabled = has_tasks
            button.tooltip = has_tasks and spec.tooltip or NO_TASKS_TOOLTIP
        end
    end

    -- Personal logistics on/off. Deliberately not in GATED_BUTTONS: it reflects
    -- and controls the player's own logistics, which is worth seeing and being
    -- able to change whether or not a Foreman list has been imported. It gates
    -- on the point existing instead.
    local toggle = frame.logistics_row and frame.logistics_row.foreman_logitoggle_btn
    if toggle then
        local state = logistics_enabled_state(player)
        if state == nil then
            toggle.enabled = false
            toggle.caption = "Logistics: n/a"
            toggle.tooltip = "Personal logistics is not available yet - it unlocks with research"
            toggle.style.font_color = { r = 0.7, g = 0.7, b = 0.7 }
        else
            toggle.enabled = true
            toggle.caption = state and "Logistics: On" or "Logistics: Off"
            toggle.tooltip = state
                and "Personal logistics is on. Click to switch it off - bots stop filling requests."
                or  "Personal logistics is off. Click to switch it on - bots start filling requests again."
            toggle.style.font_color = state and { r = 0.4, g = 1.0, b = 0.4 }
                                            or  { r = 1.0, g = 0.5, b = 0.5 }
        end
    end

    -- The caption tracks the paste box, not the task count: revealing the box again
    -- on a list that already has tasks turns "Import More" back into "Import".
    local paste  = frame.paste_section
    local import = frame.button_row and frame.button_row.foreman_import_btn
    if import then
        import.caption = (paste and paste.visible) and "Import" or "Import More"
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
        caption   = "Foreman2 Task List  (Double click item to create pin)"
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

    -- Logistics row, kept separate so the first row does not outgrow the frame
    local logi_row = frame.add{
        type      = "flow",
        name      = "logistics_row",
        direction = "horizontal"
    }
    logi_row.style.horizontal_spacing = 6

    -- Leads the row because it is a status light as much as a button: the
    -- player should see whether logistics is on before reading the actions that
    -- depend on it. Its caption and colour are set by update_button_states.
    logi_row.add{
        type    = "button",
        name    = "foreman_logitoggle_btn",
        caption = "Logistics: ?",
        tooltip = "Turn personal logistics on or off"
    }
    logi_row.add{
        type    = "button",
        name    = "foreman_requestall_btn",
        caption = "Request All",
        tooltip = "Add a personal logistic request for every unchecked task"
    }
    logi_row.add{
        type    = "button",
        name    = "foreman_clearrequests_btn",
        caption = "Clear Requests",
        tooltip = "Remove the logistic requests this mod added"
    }

    update_button_states(player)
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

        -- Sits between checkbox and label so the buttons line up in a column.
        -- Kept (disabled) on done rows rather than removed, for the same reason.
        local sprite = request_button_sprite(player)
        local req = row.add{
            type    = "sprite-button",
            name    = "task_request_" .. i,
            sprite  = sprite,
            caption = (not sprite) and "R" or nil,
            style   = "tool_button",
            enabled = not task.done,
            tooltip = "Request " .. task.count .. "x via personal logistics"
        }
        req.style.width      = 20
        req.style.height     = 20
        req.style.padding    = 0
        req.style.left_margin = 4

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
    update_button_states(player)
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
    player.zoom = 0.2

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
            end
        else
            paste_section.visible = true
        end
        refresh_gui(player)
        return
    end

    -- Clear All button
    if name == "foreman_clear_btn" then
        clear_all_task_requests(player)
        -- Clear Pins goes disabled with no tasks left, so drop the pins here too
        clear_search_tags(player)
        storage.tasks[player.index] = {}
        local frame = player.gui.screen[FRAME_NAME]
        if frame then
            frame.paste_section.visible = true
        end
        refresh_gui(player)
        return
    end

    -- Personal logistics on/off toggle
    if name == "foreman_logitoggle_btn" then
        local current = logistics_enabled_state(player)
        if current == nil then
            player.print("[Foreman2] Personal logistics cannot be switched: "
                .. (requests_blocked_reason(player) or "no requester point available") .. ".")
            refresh_gui(player)
            return
        end

        local new_state = set_logistics_enabled(player, not current)
        if new_state == nil then
            player.print("[Foreman2] Personal logistics could not be switched.")
        elseif new_state == current then
            -- Wrote it, read it back unchanged - something else owns this switch.
            player.print("[Foreman2] Personal logistics refused to switch; it is still "
                .. (current and "on" or "off") .. ".")
        else
            player.print("[Foreman2] Personal logistics switched " .. (new_state and "on" or "off") .. ".")
        end
        refresh_gui(player)
        return
    end

    -- Request All button
    if name == "foreman_requestall_btn" then
        local requested = 0
        for _, task in ipairs(storage.tasks[player.index] or {}) do
            if not task.done and request_task_item(player, task.internal_name, task.count) then
                requested = requested + 1
            end
        end
        player.print("[Foreman2] Added " .. requested .. " logistic request(s).")
        -- Checked once per click rather than per task, so a blocked Request All
        -- prints one note instead of one per row.
        local blocked = requests_blocked_reason(player)
        if blocked and requested > 0 then
            player.print("[Foreman2] These will not be filled yet - " .. blocked .. ". They stay in place and start working once that changes.")
        end
        return
    end

    -- Clear Requests button
    if name == "foreman_clearrequests_btn" then
        player.print("[Foreman2] Removed " .. clear_all_task_requests(player) .. " logistic request(s).")
        return
    end

    -- Per-task request button
    local request_idx = name:match("^task_request_(%d+)$")
    if request_idx then
        local tasks = storage.tasks[player.index]
        local task  = tasks and tasks[tonumber(request_idx)]
        if task and request_task_item(player, task.internal_name, task.count) then
            player.print("[Foreman2] Requested " .. task.count .. "x " .. (task.name or task.display) .. ".")
            local blocked = requests_blocked_reason(player)
            if blocked then
                player.print("[Foreman2] This will not be filled yet - " .. blocked .. ". It stays in place and starts working once that changes.")
            end
        end
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
            if element.state then
                clear_task_request(player, tasks[idx].internal_name)
            end
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
            if element.state then
                clear_task_request(player, tasks[idx].internal_name)
            end
            refresh_gui(player)
        end
    end
end)