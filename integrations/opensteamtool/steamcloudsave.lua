-- SteamCloudSave parking-pool registration for OpenSteamTool.
-- Ensures the SCT parking slots are always present in your unlocked library
-- (invisible/hidden apps stay listed even if the manifest bot stops updating).
-- Place beside your other .lua files in <steam>\config\lua\

addappid(480)     -- Spacewar: SCT ferry / park primary slot
addappid(113200)  -- Cloud test app: secondary slot (probed before heavy use)
addappid(250820)  -- SteamVR: candidate slot (probe first)
addappid(413080)  -- SteamVR Home: candidate slot