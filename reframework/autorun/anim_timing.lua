-- anim_timing.lua -- the in-game half of Animation & Timing (2026-11-07, Fable; UNTESTED in a real game until the home PC runs it).
--
-- Tefa, 2026-11-07: "a tool that lets me go through any of the player body's animations - like finger movement, leg movement
-- and foot angle during pushing an enemy away ... fine tune that original hand movement and placement frame by frame".
--
-- What it does: lists every animation layer playing on the player (and, in Village, on the four body meshes); FREEZES them all
-- on the frame they are at (speed 0, written every frame at UpdateMotion so the game cannot restart them), STEPS them a frame
-- at a time (set_Frame), plays them SLOW (a speed under 1), gives them back on PLAY; and turns joints by a delta on top of the
-- animation (a nudge), live from the window and saved per animation and frame in nudges/<game>/<src>_<bank>_<motion>.json,
-- interpolated between keyed frames, flat before the first and after the last. The nudges are written at LateUpdateBehavior
-- and again at PrepareRendering (the Hand Poser / EMV Engine timing) as LocalRotation = base * turn(delta), the base being the
-- animation's own rotation (read again whenever the game changed it under us).
--
-- The two halves talk through files under reframework\data\anim_timing\ (the only place REFramework Lua may read and write):
--   state.json    written here: game, character, connected, frozen, the layers (src, i, bank, motion, frame, end, speed, name),
--                 every joint as [name, parent], the live local Euler angles (degrees) of the joints the window asked about.
--   command.json  written by the window on every change: {seq, freeze=true/false, step=+1/-1 (one-shot), speed=0.05..1,
--                 set_frame={src=,i=,frame=}, nudge_live={name={x,y,z}}, want_read={names}, reload_nudges=n}.
--   nudges/<game>/<src>_<bank>_<motion>.json  written by the window: {keys={{frame=12, joints={name={x,y,z}}}, ...}}.
-- Inspired by the animation viewer in EMV Engine by alphaZomega (MIT); written fresh, nothing copied.

local DIR = "anim_timing\\"
local STATE_FILE = DIR .. "state.json"
local COMMAND_FILE = DIR .. "command.json"
local POLL_EVERY_FRAMES = 3
local STATE_EVERY_SEC = 1.0
local STATE_LIVE_SEC = 0.12          -- while frozen or anything is held / read: live numbers in the window
local RELINK_EVERY_SEC = 2.0
local MAX_LAYERS = 8
local DEG = 180.0 / math.pi

local game = (reframework and reframework.get_game_name and reframework:get_game_name()) or "unknown"
local ns = sdk.game_namespace
local motion_t = sdk.typeof("via.motion.Motion")

local last_seq = -1
local frozen = false
local speed = 1.0
local step_pending = 0               -- +1 / -1 once
local set_frame_pending = nil        -- {src=, i=, frame=}
local nudge_live = {}                -- joint name -> {x, y, z} degrees (the window's live deltas)
local want_read = {}
local reload_nudges_seen = -1
local nudge_files = {}               -- "<src>_<bank>_<motion>" -> {keys = {{frame=, joints={name={x,y,z}}}, ...}} (sorted by frame)
local nudge_files_loaded = false
local target = nil                   -- {go=, xform=, name=, joints={{obj=, name=, parent=}...}, by_name={}, motions={{mo=, src=}...}}
local saved_speed = {}               -- layer address -> the speed it had before the freeze
local last_state_t, last_relink_t, frame_n = 0, 0, 0
local last_error, write_state_now, writes = "", false, 0
local base = {}                      -- joint name -> {q = the animation's own rotation, out = what we wrote last}

local function note(err) last_error = tostring(err); write_state_now = true end
local function now() return os.clock() end
local function safe(fn) local ok, v = pcall(fn); if ok then return v end return nil end

-- ---- finding the player and its animations --------------------------------------------------------------------------

local function scene()
    local sm = sdk.get_native_singleton("via.SceneManager")
    local t = sdk.find_type_definition("via.SceneManager")
    if not sm or not t then return nil end
    return sdk.call_native_func(sm, t, "get_CurrentScene")
end

local function as_game_object(obj)
    if obj == nil then return nil end
    local td = obj:get_type_definition()
    if td and td:is_a("via.GameObject") then return obj end
    local ok, go = pcall(function() return obj:call("get_GameObject") end)
    if ok and go then return go end
    return nil
end

-- the player, by the route each RE game uses (Hand Poser's list: EMV Engine's map, re-typed)
local function find_player()
    local pm = safe(function() return sdk.get_managed_singleton("app.PropsManager") end)   -- Village (the finger recorder's route)
    local pl = pm and safe(function() return pm:get_field("<Player>k__BackingField") end)
    if pl then return pl end
    pm = sdk.get_managed_singleton(ns("PlayerManager"))
    if pm then
        local ok, p = pcall(function() return pm:call("get_CurrentPlayer") end)
        if ok and p then return as_game_object(p) end
    end
    local cm = sdk.get_managed_singleton("chainsaw.CharacterManager")   -- RE4
    if cm then
        local ok, go = pcall(function() return cm:call("getPlayerContextRef"):call("get_BodyGameObject") end)
        if ok and go then return go end
    end
    local sc = scene()
    if sc then
        for _, tname in ipairs({ "app.PlayerUpdaterBase", "app.PlayerCamera" }) do
            local t = sdk.find_type_definition(tname)
            if t then
                local ok, arr = pcall(function() return sc:call("findComponents(System.Type)", sdk.typeof(tname)) end)
                if ok and arr then
                    local els = arr:get_elements()
                    if els and els[1] then return as_game_object(els[1]) end
                end
            end
        end
    end
    return nil
end

-- Village: the body is four meshes the player updater's mesh controller owns; they carry their own motions
local function village_meshes(pl)
    local out = {}
    for _, tn in ipairs({ "app.PlayerUpdater", "app.PlayerUpdaterFPS" }) do
        local t = safe(function() return sdk.typeof(tn) end)
        local u = t and safe(function() return pl:call("getComponent(System.Type)", t) end)
        local mc = u and safe(function() return u:call("get_playerMeshController") end)
        if mc then
            for _, field in ipairs({ "UpperBodyMesh", "LowerBodyMesh", "LArmMesh", "RArmMesh" }) do
                local mesh = safe(function() return mc:get_field(field) end)
                local go = mesh and safe(function() return mesh:call("get_GameObject") end)
                if go then out[#out + 1] = { go = go, src = field } end
            end
            break
        end
    end
    return out
end

local function list_joints(xform)
    local arr = xform:call("get_Joints")
    if not arr then return nil end
    local els = arr:get_elements()
    if not els then return nil end
    local joints, by_name, index_of = {}, {}, {}
    for i, j in ipairs(els) do index_of[j:get_address()] = i - 1 end
    for i, j in ipairs(els) do
        local name = j:call("get_Name") or ("joint" .. i)
        local parent = -1
        local ok, p = pcall(function() return j:call("get_Parent") end)
        if ok and p then parent = index_of[p:get_address()] or -1 end
        joints[i] = { obj = j, name = name, parent = parent }
        if by_name[name] == nil then by_name[name] = j end
    end
    return joints, by_name
end

local function relink()
    last_relink_t = now()
    local go = find_player()
    if not go then target = nil; return end
    local ok, err = pcall(function()
        local xform = go:call("get_Transform")
        local joints, by_name = list_joints(xform)
        if not joints then error("no joints on " .. tostring(go:call("get_Name"))) end
        local motions = {}
        local mo = motion_t and safe(function() return go:call("getComponent(System.Type)", motion_t) end)
        if mo then motions[#motions + 1] = { mo = mo, src = "player" } end
        for _, m in ipairs(village_meshes(go)) do
            local mm = motion_t and safe(function() return m.go:call("getComponent(System.Type)", motion_t) end)
            if mm then motions[#motions + 1] = { mo = mm, src = m.src } end
            -- a mesh's own joints answer to the same names: kept as a second place to look
            local xf = safe(function() return m.go:call("get_Transform") end)
            if xf then
                local js, bn = safe(function() return list_joints(xf) end)
                if bn then for name, j in pairs(bn) do if by_name[name] == nil then by_name[name] = j end end end
            end
        end
        target = { go = go, xform = xform, name = go:call("get_Name") or "?", joints = joints, by_name = by_name, motions = motions }
    end)
    if not ok then target = nil; note(err) end
    write_state_now = true
end

local function target_alive()
    if not target then return false end
    local ok, alive = pcall(function() return target.xform ~= nil and target.go:call("get_Valid") ~= false end)
    return ok and alive
end

-- ---- the layers ---------------------------------------------------------------------------------------------------------

local function each_layer(fn)   -- fn(layer, src, i)
    if not target then return end
    for _, m in ipairs(target.motions) do
        local n = safe(function() return m.mo:call("getLayerCount") end) or 0
        for i = 0, math.min(n, MAX_LAYERS) - 1 do
            local L = safe(function() return m.mo:call("getLayer", i) end)
            if L then fn(L, m.src, i) end
        end
    end
end

local function layer_info(L, src, i)
    local bank = safe(function() return L:call("get_MotionBankID") end) or -1
    local id = safe(function() return L:call("get_MotionID") end) or -1
    local fr = safe(function() return L:call("get_Frame") end) or -1
    local e = safe(function() return L:call("get_EndFrame") end) or -1
    local sp = safe(function() return L:call("get_Speed") end) or -1
    local name = ""
    pcall(function()
        local node = L:call("get_HighestWeightMotionNode")
        if node then name = tostring(node:call("get_MotionName") or "") end
    end)
    return { src = src, i = i, bank = tonumber(bank) or -1, motion = tonumber(id) or -1, frame = tonumber(fr) or -1, ["end"] = tonumber(e) or -1, speed = tonumber(sp) or -1, name = name }
end

-- the freeze, the slow motion and the steps: at UpdateMotion (pre), before the animation advances this frame
local function drive_layers()
    if not target then return end
    if not frozen and speed >= 0.999 then
        if next(saved_speed) ~= nil then   -- just let go: every layer gets its speed back
            each_layer(function(L)
                local s = saved_speed[L:get_address()]
                if s ~= nil then pcall(function() L:call("set_Speed", s) end) end
            end)
            saved_speed = {}
        end
        return
    end
    each_layer(function(L, src, i)
        local a = L:get_address()
        if saved_speed[a] == nil then saved_speed[a] = safe(function() return L:call("get_Speed") end) or 1.0 end
        pcall(function() L:call("set_Speed", frozen and 0.0 or speed) end)
        if frozen and step_pending ~= 0 then
            local fr = safe(function() return L:call("get_Frame") end) or 0
            local e = safe(function() return L:call("get_EndFrame") end) or fr
            local nf = fr + step_pending
            if nf < 0 then nf = 0 end
            if e > 0 and nf > e then nf = e end
            pcall(function() L:call("set_Frame", nf) end)
        end
        if set_frame_pending and set_frame_pending.src == src and set_frame_pending.i == i then
            pcall(function() L:call("set_Frame", set_frame_pending.frame) end)
        end
    end)
    step_pending = 0
    set_frame_pending = nil
    write_state_now = true
end

-- ---- the nudges -----------------------------------------------------------------------------------------------------------

local function load_nudge_files()
    nudge_files = {}
    local list = safe(function() return fs.glob("anim_timing/nudges/" .. game .. "/.*\\.json") end)   -- (fs.glob takes a regex on paths under data)
    if type(list) ~= "table" then nudge_files_loaded = true; return end
    local n = 0
    for _, path in ipairs(list) do
        local d = safe(function() return json.load_file(path) end)
        if type(d) == "table" and type(d.keys) == "table" then
            local key = path:match("([^/\\]+)%.json$")
            table.sort(d.keys, function(a, b) return (a.frame or 0) < (b.frame or 0) end)
            nudge_files[key] = d
            n = n + 1
        end
    end
    nudge_files_loaded = true
    log.info("[anim_timing] " .. n .. " nudge file(s) loaded for " .. game)
end

local function lerp3(a, b, u)
    a = a or { 0, 0, 0 }; b = b or { 0, 0, 0 }
    return { a[1] + (b[1] - a[1]) * u, a[2] + (b[2] - a[2]) * u, a[3] + (b[3] - a[3]) * u }
end

-- this frame's saved deltas: for every layer playing an animation that has a nudge file, the keys around its frame
local function saved_deltas()
    local out = {}
    if next(nudge_files) == nil then return out end
    each_layer(function(L, src, i)
        local bank = tonumber(safe(function() return L:call("get_MotionBankID") end)) or -1
        local id = tonumber(safe(function() return L:call("get_MotionID") end)) or -1
        local d = nudge_files[string.format("%s_%d_%d", src, bank, id)]
        if d and #d.keys > 0 then
            local fr = tonumber(safe(function() return L:call("get_Frame") end)) or 0
            local lo, hi = d.keys[1], d.keys[#d.keys]
            for k = 1, #d.keys do
                if d.keys[k].frame <= fr then lo = d.keys[k] end
                if d.keys[k].frame >= fr then hi = d.keys[k]; break end
            end
            if lo.frame > fr then lo = hi end
            if hi.frame < fr then hi = lo end
            local u = (hi.frame > lo.frame) and (fr - lo.frame) / (hi.frame - lo.frame) or 0
            local names = {}
            for name in pairs(lo.joints or {}) do names[name] = true end
            for name in pairs(hi.joints or {}) do names[name] = true end
            for name in pairs(names) do
                local v = lerp3((lo.joints or {})[name], (hi.joints or {})[name], u)
                local o = out[name]
                if o then out[name] = { o[1] + v[1], o[2] + v[2], o[3] + v[3] } else out[name] = v end
            end
        end
    end)
    return out
end

-- a turn from Euler degrees in the engine's own order (YXZ: measured 2026-11-05 in Village), as x, y, z, w
local function quat_yxz(d)
    local r = math.pi / 360
    local sy, cy = math.sin(d[2] * r), math.cos(d[2] * r)
    local sx, cx = math.sin(d[1] * r), math.cos(d[1] * r)
    local sz, cz = math.sin(d[3] * r), math.cos(d[3] * r)
    -- qy * qx
    local ax, ay, az, aw = cy * sx, sy * cx, -sy * sx, cy * cx
    -- (qy*qx) * qz
    return { x = ax * cz + ay * sz, y = ay * cz - ax * sz, z = aw * sz + az * cz, w = aw * cz - az * sz }
end
local function qmul(a, b)
    return { x = a.w * b.x + a.x * b.w + a.y * b.z - a.z * b.y, y = a.w * b.y - a.x * b.z + a.y * b.w + a.z * b.x,
             z = a.w * b.z + a.x * b.y - a.y * b.x + a.z * b.w, w = a.w * b.w - a.x * b.x - a.y * b.y - a.z * b.z }
end
local function same_q(a, b)
    if not a or not b then return false end
    return math.abs(a.x - b.x) + math.abs(a.y - b.y) + math.abs(a.z - b.z) + math.abs(a.w - b.w) < 1e-4
end

local function apply()
    if not target then return end
    local deltas = saved_deltas()
    for name, v in pairs(nudge_live) do   -- the window's live delta on a joint replaces that joint's saved one (so a key is never doubled)
        deltas[name] = v
    end
    local n = 0
    for name, d in pairs(deltas) do
        if math.abs(d[1]) + math.abs(d[2]) + math.abs(d[3]) > 1e-4 then
            local j = target.by_name[name]
            if j then
                local ok = pcall(function()
                    local cur = j:call("get_LocalRotation")
                    local b = base[name]
                    local anim = (b and same_q(cur, b.out)) and b.q or cur   -- the game rewrote it: that is the new base
                    local out = qmul({ x = anim.x, y = anim.y, z = anim.z, w = anim.w }, quat_yxz(d))
                    j:call("set_LocalRotation", Quaternion.new(out.x, out.y, out.z, out.w))
                    base[name] = { q = { x = anim.x, y = anim.y, z = anim.z, w = anim.w }, out = out }
                end)
                if not ok then target = nil; return end
                n = n + 1
            end
        else
            base[name] = nil
        end
    end
    _G.anim_timing_nudged = n
end

-- ---- the two files -----------------------------------------------------------------------------------------------------------

local function read_command()
    local cmd = json.load_file(COMMAND_FILE)
    if type(cmd) ~= "table" or cmd.seq == nil or cmd.seq == last_seq then return end
    last_seq = cmd.seq
    if cmd.freeze ~= nil then frozen = cmd.freeze == true end
    if type(cmd.speed) == "number" then speed = math.max(0.0, math.min(1.0, cmd.speed)) end
    if type(cmd.step) == "number" and cmd.step ~= 0 then step_pending = cmd.step > 0 and 1 or -1 end
    if type(cmd.set_frame) == "table" and cmd.set_frame.src and cmd.set_frame.i and cmd.set_frame.frame then
        set_frame_pending = { src = tostring(cmd.set_frame.src), i = math.floor(cmd.set_frame.i), frame = cmd.set_frame.frame }
    end
    nudge_live = {}
    for name, v in pairs(cmd.nudge_live or {}) do
        if type(v) == "table" and #v == 3 then nudge_live[name] = { v[1], v[2], v[3] } end
    end
    want_read = {}
    for _, name in ipairs(cmd.want_read or {}) do want_read[#want_read + 1] = name end
    if type(cmd.reload_nudges) == "number" and cmd.reload_nudges ~= reload_nudges_seen then
        reload_nudges_seen = cmd.reload_nudges
        pcall(load_nudge_files)
    end
    write_state_now = true
end

local function angles_of(name)
    local j = target and target.by_name[name]
    if not j then return nil end
    local ok, e = pcall(function() return j:call("get_LocalEulerAngle") end)
    if not ok or not e then return nil end
    return { e.x * DEG, e.y * DEG, e.z * DEG }
end

local function write_state()
    last_state_t = now()
    write_state_now = false
    local st = {
        game = game, character = target and target.name or "", connected = target ~= nil,
        frozen = frozen, speed = speed, seq_seen = last_seq, frame = frame_n, error = last_error,
        layers = {}, joints = {}, angles = {}, nudge_files = {},
    }
    if target then
        each_layer(function(L, src, i) st.layers[#st.layers + 1] = layer_info(L, src, i) end)
        for i, j in ipairs(target.joints) do st.joints[i] = { j.name, j.parent } end
        for _, name in ipairs(want_read) do st.angles[name] = angles_of(name) end
        for name in pairs(nudge_live) do if st.angles[name] == nil then st.angles[name] = angles_of(name) end end
        for key, d in pairs(nudge_files) do st.nudge_files[#st.nudge_files + 1] = { key, #d.keys } end
    end
    local ok, err = pcall(function() json.dump_file(STATE_FILE, st, 0) end)
    if ok then writes = writes + 1 else last_error = tostring(err) end
end

re.on_frame(function()
    frame_n = frame_n + 1
    if frame_n % POLL_EVERY_FRAMES == 0 then
        local ok, err = pcall(read_command)
        if not ok then note(err) end
    end
    if not target_alive() then
        target = nil
        if now() - last_relink_t > RELINK_EVERY_SEC then relink() end
    end
    if not nudge_files_loaded then pcall(load_nudge_files) end
    local live = frozen or speed < 0.999 or next(nudge_live) ~= nil or #want_read > 0
    local every = live and STATE_LIVE_SEC or STATE_EVERY_SEC
    if write_state_now or now() - last_state_t > every then
        local ok, err = pcall(write_state)
        if not ok then last_error = tostring(err) end
    end
end)

re.on_pre_application_entry("UpdateMotion", function() pcall(drive_layers) end)
re.on_application_entry("LateUpdateBehavior", function() pcall(apply) end)
re.on_application_entry("PrepareRendering", function() pcall(apply) end)

re.on_draw_ui(function()
    if imgui.tree_node("Animation & Timing") then
        local nl = 0
        each_layer(function() nl = nl + 1 end)
        imgui.text(string.format("%s  %s  %s  layers %d  speed %.2f  nudged %d  seq %d  writes %d", game, target and target.name or "(no character)",
            frozen and "FROZEN" or "playing", nl, speed, _G.anim_timing_nudged or 0, last_seq, writes))
        if last_error ~= "" then imgui.text("last error: " .. last_error) end
        if imgui.button(frozen and "Play" or "Freeze") then frozen = not frozen; write_state_now = true end
        if frozen then
            imgui.same_line()
            if imgui.button("-1 frame") then step_pending = -1 end
            imgui.same_line()
            if imgui.button("+1 frame") then step_pending = 1 end
        end
        if imgui.button("Let go of every nudge") then nudge_live = {}; nudge_files = {}; write_state_now = true end
        imgui.tree_pop()
    end
end)

log.info("[anim_timing] ready: " .. game)
