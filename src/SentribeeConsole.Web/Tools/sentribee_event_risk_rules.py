import math

MACHINERY_LABELS = {"machinery_vehicle", "excavator", "crane", "forklift", "truck"}
HEIGHT_LABELS = {"ladder", "scaffold"}
DANGEROUS_SCENE_LABELS = {"fire_smoke", "vehicle_accident", "machinery_accident"}
PPE_RISK_EQUIVALENTS = {
    "no_vest": "vest",
}


def box_area(box):
    return max(0.0, box[2] - box[0]) * max(0.0, box[3] - box[1])


def box_iou(first, second):
    ix1 = max(first[0], second[0])
    iy1 = max(first[1], second[1])
    ix2 = min(first[2], second[2])
    iy2 = min(first[3], second[3])
    inter = max(0.0, ix2 - ix1) * max(0.0, iy2 - iy1)
    union = box_area(first) + box_area(second) - inter
    return inter / union if union > 0 else 0.0


def normalized_gap(first, second, width, height):
    left_gap = max(first[0] - second[2], second[0] - first[2], 0.0)
    top_gap = max(first[1] - second[3], second[1] - first[3], 0.0)
    return math.hypot(left_gap, top_gap) / max(float(width), float(height), 1.0)


def xyxy_to_box_object(box, label=None, confidence=None):
    x1, y1, x2, y2 = box
    result = {
        "x": round(x1, 2),
        "y": round(y1, 2),
        "w": round(x2 - x1, 2),
        "h": round(y2 - y1, 2),
    }
    if label:
        result["label"] = label
    if confidence is not None:
        result["confidence"] = round(float(confidence), 4)
    return result


def risk_zone_match(person_box, width, height, risk_zones):
    foot = [((person_box[0] + person_box[2]) / 2.0) / width, person_box[3] / height]
    for index, zone in enumerate(risk_zones or []):
        box = zone.get("box") or zone.get("bbox") or zone.get("rect") if isinstance(zone, dict) else zone
        name = zone.get("name") or zone.get("id") or f"risk_zone_{index + 1}" if isinstance(zone, dict) else f"risk_zone_{index + 1}"
        if isinstance(box, list) and len(box) == 4 and box[0] <= foot[0] <= box[2] and box[1] <= foot[1] <= box[3]:
            return name
    return None


def effective_ppe_labels(ppe):
    labels = set(ppe or {})
    labels.update(PPE_RISK_EQUIVALENTS[label] for label in list(labels) if label in PPE_RISK_EQUIVALENTS)
    return labels


def assess_person_risk(
    person,
    scene_objects,
    required_ppe,
    width,
    height,
    high_work_marks=None,
    risk_zones=None,
    machinery_high_distance=0.04,
    machinery_low_distance=0.08,
    machinery_overlap_iou=0.01,
):
    high_work_marks = high_work_marks or {}
    person_box = person["box"]
    present_ppe = effective_ppe_labels(person.get("ppe", {}))
    missing = [item for item in required_ppe if item not in present_ppe]
    near_height = []
    near_machinery = []
    risk_reasons = []
    risk_factors = []

    for obj in scene_objects or []:
        gap = normalized_gap(person_box, obj["box"], width, height)
        iou = box_iou(person_box, obj["box"])
        context = {
            "type": obj["label"],
            "box": xyxy_to_box_object(obj["box"], obj["label"], obj.get("confidence")),
            "normalizedGap": round(gap, 4),
            "iou": round(iou, 4),
        }
        if obj["label"] in MACHINERY_LABELS and (
            gap <= machinery_low_distance or iou >= machinery_overlap_iou
        ):
            near_machinery.append(context)
            risk_factors.append("near_heavy_machinery")
            if gap <= machinery_high_distance or iou >= machinery_overlap_iou:
                risk_reasons.append("machinery_high_radiation")
        if obj["label"] in HEIGHT_LABELS and (
            gap <= machinery_low_distance or iou >= machinery_overlap_iou
        ):
            near_height.append(context)
            risk_factors.append("near_ladder_or_scaffold")

    high_work = high_work_marks.get(person["id"], {})
    at_height = bool(high_work or near_height and person_box[3] / height < 0.72)
    risk_zone = risk_zone_match(person_box, width, height, risk_zones)
    if risk_zone:
        risk_reasons.append("risk_zone")
        risk_factors.append("risk_zone")
    if at_height:
        risk_reasons.append(high_work.get("height_risk") or "fall_risk_area")
        risk_factors.append("at_height")
    if at_height and missing:
        risk_reasons.append("at_height_missing_ppe")
    elif near_height and missing:
        risk_reasons.append("near_ladder_or_scaffold_missing_ppe")
    if missing:
        risk_reasons.append("missing_required_ppe")
        risk_factors.append("missing_required_ppe")

    has_scaffold_context = any(item["type"] == "scaffold" for item in near_height)
    high_work_without_scaffold = bool(at_height and not has_scaffold_context)
    is_major = bool(at_height and missing or high_work_without_scaffold)
    is_risk = bool(is_major or missing or risk_zone or risk_reasons or near_machinery and missing)
    severity = "Severe Danger" if is_major else "Ordinary Risk" if is_risk else "No Risk"
    category = "High work PPE risk" if is_major else "Fall-risk area" if at_height else "PPE/person risk" if missing else "Safety review"
    if is_major:
        if missing:
            reason = f"Person is in a fall-risk/high-work area and missing required PPE: {', '.join(missing)}."
        else:
            reason = "Person appears to be working at height without scaffold protection."
    elif risk_reasons:
        reason = "Risk context detected: " + ", ".join(dict.fromkeys(risk_reasons)) + ("." if not missing else f"; missing PPE: {', '.join(missing)}.")
    elif missing:
        reason = f"Missing required PPE: {', '.join(missing)}."
    else:
        reason = "No major risk detected for this person."

    return {
        "missingPpe": missing,
        "ppeComplete": not missing,
        "nearHeightObjects": near_height,
        "nearMachinery": near_machinery,
        "atHeight": at_height,
        "riskZoneName": risk_zone,
        "riskFactors": list(dict.fromkeys(risk_factors + risk_reasons)),
        "isRisk": is_risk,
        "riskSeverity": severity,
        "riskCategory": category,
        "riskReason": reason,
        "highWorkReview": high_work or None,
    }


def classify_event_risk(people_count, scene_objects, person_risks):
    scene_objects = scene_objects or []
    person_risks = person_risks or []
    dangerous_scene = [
        item for item in scene_objects
        if item.get("label") in DANGEROUS_SCENE_LABELS
    ]
    severe_people = [item for item in person_risks if item.get("riskSeverity") == "Severe Danger"]
    ordinary_people = [item for item in person_risks if item.get("riskSeverity") == "Ordinary Risk"]
    risk_people = severe_people + ordinary_people

    if dangerous_scene or severe_people:
        return {
            "status": "Severe Danger",
            "riskSeverity": "Severe Danger",
            "riskCategory": "Severe site safety danger",
            "riskPersonCount": len(risk_people),
            "summary": build_event_summary(
                "Severe danger detected",
                people_count,
                scene_objects,
                severe_people,
                dangerous_scene,
            ),
        }

    if ordinary_people:
        return {
            "status": "Ordinary Risk",
            "riskSeverity": "Ordinary Risk",
            "riskCategory": "PPE compliance risk",
            "riskPersonCount": len(ordinary_people),
            "summary": build_event_summary(
                "Ordinary PPE risk detected",
                people_count,
                scene_objects,
                ordinary_people,
                dangerous_scene,
            ),
        }

    if people_count <= 0:
        return {
            "status": "Invalid Event",
            "riskSeverity": "Invalid Event",
            "riskCategory": "Invalid event",
            "riskPersonCount": 0,
            "summary": build_event_summary(
                "No valid safety event detected",
                people_count,
                scene_objects,
                [],
                dangerous_scene,
            ),
        }

    return {
        "status": "No Risk",
        "riskSeverity": "No Risk",
        "riskCategory": "Site safety clear",
        "riskPersonCount": 0,
        "summary": build_event_summary(
            "No risk detected",
            people_count,
            scene_objects,
            [],
            dangerous_scene,
        ),
    }


def build_event_summary(prefix, people_count, scene_objects, risk_people, dangerous_scene):
    scene_labels = sorted({item.get("label") for item in scene_objects if item.get("label")})
    dangerous_labels = sorted({item.get("label") for item in dangerous_scene if item.get("label")})
    parts = [
        f"{prefix}.",
        f"People: {people_count}.",
        f"Risk people: {len(risk_people)}.",
        f"Scene objects: {', '.join(scene_labels) if scene_labels else 'none'}.",
    ]
    if dangerous_labels:
        parts.append(f"Dangerous scene: {', '.join(dangerous_labels)}.")
    return " ".join(parts)
