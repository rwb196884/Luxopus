#!/bin/sh

planDir="/home/rwb/Luxopus/plans"
f=$(ls "$planDir" | tail -1)

cat "${planDir}/$f" | jq '.Plans | map (if .Buy > 30 then . | .Action.DischargeToGrid |= 100 | .Action.ChargeFromGrid |= 0 else
 . | .Action.DischargeToGrid |= 100 | .Action.ChargeFromGrid |= 100 end)' #> "${planDir}/${f}.tmp"
#mv "${planDir}/${f}.tmp" "${planDir}/$f"

