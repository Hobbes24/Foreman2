-- control.lua
-- Foreman2 Task List mod

local FRAME_NAME = "foreman_tasklist_frame"
local DOUBLE_CLICK_TICKS = 30  -- ~0.5 seconds at 60UPS
local TAG_PREFIX = "[F2] "     -- prefix on all our chart tags so we can find and remove them

-- ============================================================
-- Storage init
-- ============================================================

script.on_init(function()
    storage.tasks = {}
    storage.last_click = {}
end)

script.on_configuration_changed(function()
    storage.tasks = storage.tasks or {}
    storage.last_click = storage.last_click or {}
end)

-- ============================================================
-- Toggle UI on keybind
-- ============================================================

script.on_event("foreman-tasklist-toggle", function(event)
    local player = game.players[event.player_index]
    local frame = player.gui.screen[FRAME_NAME]
    if frame then
        frame.destroy()
    else
        build_gui(player)
    end
end)

-- ============================================================
-- Build the GUI
-- ============================================================

function build_gui(player)
    local frame = player.gui.screen.add{
        type = "frame",
        name = FRAME_NAME,
        direction = "vertical",
        caption = "Foreman2 Task List"
    }
    frame.auto_center = true

    local scroll = frame.add{
        type = "scroll-pane",
        name = "task_scroll",
        direction = "vertical"
    }
    scroll.style.maximal_height = 400
    scroll.style.minimal_width = 350
    scroll.style.padding = 4

    render_tasks(scroll)

    frame.add{ type = "line" }

    local paste_section = frame.add{
        type = "flow",
        name = "paste_section",
        direction = "vertical"
    }
    paste_section.add{
        type = "label",
        caption = "Paste Foreman2 export here, then click Import:"
    }
    local textbox = paste_section.add{
        type = "text-box",
        name = "paste_box"
    }
    textbox.style.width = 350
    textbox.style.height = 100

    paste_section.visible = (#storage.tasks == 0)

    frame.add{ type = "line" }

    frame.add{
        type = "label",
        name = "hint_label",
        caption = "[color=150,150,150][italic]Double-click a task to find buildings on map[/italic][/color]"
    }

    local buttons = frame.add{
        type = "flow",
        name = "button_row",
        direction = "horizontal"
    }
    local import_caption = (#storage.tasks == 0) and "Import" or "Import More"
    buttons.add{ type = "button", name = "foreman_import_btn",     caption = import_caption }
    buttons.add{ type = "button", name = "foreman_clear_btn",      caption = "Clear All"   }
    buttons.add{ type = "button", name = "foreman_clear_pins_btn", caption = "Clear Pins"  }
    buttons.add{ type = "button", name = "foreman_close_btn",      caption = "Close"       }
end

-- ============================================================
-- Render task checkboxes
-- ============================================================

function render_tasks(scroll)
    scroll.clear()
    if #storage.tasks == 0 then
        scroll.add{
            type = "label",
            name = "empty_label",
            caption = "[italic]No tasks yet. Paste a list and click Import.[/italic]"
        }
        return
    end
    for i, task in ipairs(storage.tasks) do
        local display = task.display_name or task.text
        local cb = scroll.add{
            type = "checkbox",
            name = "task_" .. i,
            caption = display,
            state = task.done
        }
        if task.done then
            cb.style.font_color = { r = 0.5, g = 0.5, b = 0.5 }
        end
    end
end

-- ============================================================
-- Parse pasted text
-- Format: "12x Friendly Name [internal-name]"
-- ============================================================

function parse_and_append(text)
    for line in text:gmatch("[^\r\n]+") do
        line = line:match("^%s*(.-)%s*$")
        if line ~= "" then
            local internal_name = line:match("%[(.-)%]$")
            local display_name
            if internal_name then
                display_name = line:match("^(.-)%s*%[.-%]$")
            else
                display_name = line
            end
            table.insert(storage.tasks, {
                text          = line,
                display_name  = display_name,
                internal_name = internal_name,
                done          = false
            })
        end
    end
end

-- ============================================================
-- Map search
-- ============================================================

function search_for_entity(player, internal_name, display_name)
    clear_search_tags(player)

    local found = player.surface.find_entities_filtered{ name = internal_name }

    -- Pyanodon's '-turd' suffix denotes a recipe variant, not a distinct building.
    -- If no entities found, fall back to the base building name.
    local search_name = internal_name
    if #found == 0 and internal_name:sub(-5) == "-turd" then
        search_name = internal_name:sub(1, -6)
        found = player.surface.find_entities_filtered{ name = search_name }
    end

    if #found == 0 then
        player.print("[Foreman2] No '" .. display_name .. "' found on this surface.")
        return
    end

    for _, machine in ipairs(found) do
        player.force.add_chart_tag(player.surface, {
            position = machine.position,
            icon     = { type = "entity", name = search_name },
            text     = TAG_PREFIX .. display_name
        })
    end

    player.set_controller{
        type     = defines.controllers.remote,
        position = found[1].position,
        surface  = player.surface
    }

    player.print("[Foreman2] Found " .. #found .. " " .. display_name .. " — map pins added.")
end

function clear_search_tags(player)
    -- Find all chart tags on this surface belonging to this force,
    -- and remove any that were placed by this mod (identified by TAG_PREFIX)
    local all_tags = player.force.find_chart_tags(player.surface)
    for _, tag in pairs(all_tags) do
        if tag.text:sub(1, #TAG_PREFIX) == TAG_PREFIX then
            tag.destroy()
        end
    end
end

-- ============================================================
-- GUI click handler
-- ============================================================

script.on_event(defines.events.on_gui_click, function(event)
    local el = event.element
    if not el or not el.valid then return end

    local player = game.players[event.player_index]
    local frame  = player.gui.screen[FRAME_NAME]
    if not frame then return end

    local paste_section = frame.paste_section
    local import_btn    = frame.button_row.foreman_import_btn

    -- Check for double-click on a task checkbox
    local task_idx = el.name:match("^task_(%d+)$")
    if task_idx then
        local now  = game.tick
        local last = storage.last_click[event.player_index]

        if last and last.name == el.name and (now - last.tick) <= DOUBLE_CLICK_TICKS then
            storage.last_click[event.player_index] = nil
            local idx  = tonumber(task_idx)
            local task = storage.tasks[idx]
            if task and task.internal_name then
                search_for_entity(player, task.internal_name, task.display_name or task.text)
            else
                player.print("[Foreman2] No internal entity name available — was this imported from Foreman2?")
            end
        else
            storage.last_click[event.player_index] = { name = el.name, tick = now }
        end
        return
    end

    if el.name == "foreman_import_btn" then
        if paste_section.visible then
            local text = paste_section.paste_box.text
            if text and text ~= "" then
                parse_and_append(text)
                paste_section.paste_box.text = ""
            end
            if #storage.tasks > 0 then
                paste_section.visible = false
                import_btn.caption = "Import More"
            end
            render_tasks(frame.task_scroll)
        else
            paste_section.visible = true
            import_btn.caption = "Import"
        end

    elseif el.name == "foreman_clear_btn" then
        storage.tasks = {}
        paste_section.visible = true
        import_btn.caption = "Import"
        render_tasks(frame.task_scroll)

    elseif el.name == "foreman_clear_pins_btn" then
        clear_search_tags(player)
        player.print("[Foreman2] Map pins cleared.")

    elseif el.name == "foreman_close_btn" then
        frame.destroy()
    end
end)

-- ============================================================
-- Checkbox state change
-- ============================================================

script.on_event(defines.events.on_gui_checked_state_changed, function(event)
    local el = event.element
    if not el or not el.valid then return end

    local idx = el.name:match("^task_(%d+)$")
    if idx then
        idx = tonumber(idx)
        if storage.tasks[idx] then
            storage.tasks[idx].done = el.state
            if el.state then
                el.style.font_color = { r = 0.5, g = 0.5, b = 0.5 }
            else
                el.style.font_color = { r = 1, g = 1, b = 1 }
            end
        end
    end
end)
