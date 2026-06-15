#!/usr/bin/env python3
import argparse
import base64
import hashlib
import json
import math
import os
import re
import sys
from pathlib import Path

import cv2
import numpy as np

import sentribee_event_risk_rules as risk_rules

try:
    from ultralytics import YOLO
except Exception:  # pragma: no cover - surfaced in CLI diagnostics
    YOLO = None


CLASSES = [
    "helmet",
    "gloves",
    "vest",
    "boots",
    "goggles",
    "none",
    "Person",
    "no_helmet",
    "no_goggle",
    "no_gloves",
    "no_boots",
    "no_vest",
    "machinery_vehicle",
    "excavator",
    "crane",
    "forklift",
    "truck",
    "scaffold",
    "ladder",
    "rebar",
    "uncapped_rebar",
    "fire_smoke",
]


def class_key(name):
    return re.sub(r"[^a-z0-9]+", "_", str(name).lower()).strip("_")


CLASS_ID = {class_key(name): index for index, name in enumerate(CLASSES)}
PPE_LABELS = {"helmet", "vest", "goggles", "gloves", "boots"}
NEGATIVE_PPE_LABELS = {"no_helmet", "no_goggle", "no_gloves", "no_boots", "no_vest"}
PPE_DETECTION_LABELS = PPE_LABELS | NEGATIVE_PPE_LABELS
REQUIRED_DEFAULT = ["helmet", "vest"]
LOW_CONF_MACHINERY_PERSON_CONF = 0.05

PPE_ALIASES = {
    "helmet": {"helmet", "hardhat", "hard_hat", "hat", "safety_helmet"},
    "vest": {"vest", "safety_vest", "reflective_vest", "hi_vis_vest", "high_visibility_vest"},
    "goggles": {"goggles", "glasses", "safety_glasses"},
    "gloves": {"gloves", "glove"},
    "boots": {"boots", "shoes", "safety_shoes", "safety_boots"},
}
SCENE_ALIASES = {
    "machinery_vehicle": {
        "car",
        "motorcycle",
        "bus",
        "truck",
        "machinery_vehicle",
        "vehicle",
        "plant",
        "machine",
    },
    "excavator": {
        "excavator",
        "digger",
        "loader",
        "heavy_machinery",
        "dangerous_equipment",
    },
    "crane": {
        "crane",
        "tower_crane",
        "mobile_crane",
    },
    "forklift": {
        "forklift",
        "telehandler",
    },
    "truck": {
        "truck",
        "dump_truck",
        "lorry",
    },
    "ladder": {"ladder"},
    "scaffold": {"scaffold", "scaffolding", "work_platform", "platform"},
    "rebar": {"rebar", "steel_bar"},
    "uncapped_rebar": {"uncapped_rebar", "exposed_rebar"},
    "fire_smoke": {"fire", "smoke", "fire_smoke"},
}


def normalize_label(name):
    raw = re.sub(r"[^a-z0-9]+", "_", str(name).lower()).strip("_")
    negative_aliases = {
        "nohardhat": "no_helmet",
        "no_hat": "no_helmet",
        "no_hardhat": "no_helmet",
        "no_safety_helmet": "no_helmet",
        "novest": "no_vest",
        "no_safety_vest": "no_vest",
        "no_reflective_vest": "no_vest",
        "no_hi_vis_vest": "no_vest",
        "no_high_visibility_vest": "no_vest",
    }
    if raw in NEGATIVE_PPE_LABELS:
        return raw
    if raw in negative_aliases:
        return negative_aliases[raw]
    if raw.startswith(("no_", "not_", "without_", "missing_")):
        return None
    for label, aliases in PPE_ALIASES.items():
        if raw in aliases:
            return label
    for label, aliases in SCENE_ALIASES.items():
        if raw in aliases:
            return label
    if raw in {"person", "worker"}:
        return "person"
    return raw


def clamp_box(box, width, height):
    if len(box or []) != 4:
        return None
    x1, y1, x2, y2 = [float(value) for value in box]
    x1 = max(0.0, min(float(width - 1), x1))
    y1 = max(0.0, min(float(height - 1), y1))
    x2 = max(0.0, min(float(width), x2))
    y2 = max(0.0, min(float(height), y2))
    if x2 <= x1 or y2 <= y1:
        return None
    return [x1, y1, x2, y2]


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


def box_overlap_ratio(first, second):
    ix1 = max(first[0], second[0])
    iy1 = max(first[1], second[1])
    ix2 = min(first[2], second[2])
    iy2 = min(first[3], second[3])
    inter = max(0.0, ix2 - ix1) * max(0.0, iy2 - iy1)
    return inter / max(min(box_area(first), box_area(second)), 1e-6)


def box_center_distance(first, second):
    first_cx = (first[0] + first[2]) / 2.0
    first_cy = (first[1] + first[3]) / 2.0
    second_cx = (second[0] + second[2]) / 2.0
    second_cy = (second[1] + second[3]) / 2.0
    return math.hypot(first_cx - second_cx, first_cy - second_cy)


def normalized_gap(first, second, width, height):
    left_gap = max(first[0] - second[2], second[0] - first[2], 0.0)
    top_gap = max(first[1] - second[3], second[1] - first[3], 0.0)
    return math.hypot(left_gap, top_gap) / max(float(width), float(height), 1.0)


def is_duplicate_detection(item, old, iou_threshold):
    if item["label"] != old["label"]:
        return False

    iou = box_iou(item["box"], old["box"])
    if item["label"] != "person":
        return iou >= iou_threshold or box_overlap_ratio(item["box"], old["box"]) >= 0.65

    overlap = box_overlap_ratio(item["box"], old["box"])
    if iou >= 0.25 or overlap >= 0.65:
        return True

    item_area = box_area(item["box"])
    old_area = box_area(old["box"])
    larger_side = max(
        old["box"][2] - old["box"][0],
        old["box"][3] - old["box"][1],
        item["box"][2] - item["box"][0],
        item["box"][3] - item["box"][1],
        1.0,
    )
    area_ratio = min(item_area, old_area) / max(item_area, old_area, 1e-6)
    return area_ratio >= 0.35 and box_center_distance(item["box"], old["box"]) <= larger_side * 0.28


def merge_detection_metadata(target, item):
    source = str(item.get("source") or "")
    target_sources = set(target.get("sources") or [])
    if target.get("source"):
        target_sources.add(target["source"])
    if source:
        target_sources.add(source)
    if target_sources:
        target["sources"] = sorted(target_sources)

    if source == "edge_detection_payload" or item.get("edgePayloadConfirmed"):
        target["edgePayloadConfirmed"] = True
        for key in ("edgePayloadNote", "edgeEventType", "edgeMissingPpe", "edgeRiskReasons"):
            if item.get(key) and not target.get(key):
                target[key] = item[key]


def dedupe(items, iou_threshold=0.55):
    kept = []
    for item in sorted(items, key=lambda value: value.get("confidence", 0), reverse=True):
        duplicate = next((old for old in kept if is_duplicate_detection(item, old, iou_threshold)), None)
        if duplicate:
            merge_detection_metadata(duplicate, item)
        else:
            kept.append(item)
    return kept


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
        result["classId"] = CLASS_ID.get(class_key(label), -1)
    if confidence is not None:
        result["confidence"] = round(float(confidence), 4)
    return result


def crop_from_box(image, box):
    x1, y1, x2, y2 = [int(round(value)) for value in box]
    return image[y1:y2, x1:x2]


def yolo_line(label, box, width, height):
    x1, y1, x2, y2 = box
    return {
        "classId": CLASS_ID[class_key(label)],
        "label": label,
        "xCenter": round(((x1 + x2) / 2.0) / width, 6),
        "yCenter": round(((y1 + y2) / 2.0) / height, 6),
        "width": round((x2 - x1) / width, 6),
        "height": round((y2 - y1) / height, 6),
    }


def yolo_text(label_rows):
    return "\n".join(
        f"{row['classId']} {row['xCenter']:.6f} {row['yCenter']:.6f} {row['width']:.6f} {row['height']:.6f}"
        for row in label_rows
    )


def expand_box(box, width, height, scale, min_side):
    x1, y1, x2, y2 = box
    cx = (x1 + x2) / 2.0
    cy = (y1 + y2) / 2.0
    bw = max(x2 - x1, float(min_side))
    bh = max(y2 - y1, float(min_side))
    side_w = bw * float(scale)
    side_h = bh * float(scale)
    return [
        max(0.0, cx - side_w / 2.0),
        max(0.0, cy - side_h / 2.0),
        min(float(width), cx + side_w / 2.0),
        min(float(height), cy + side_h / 2.0),
    ]


def adaptive_person_crop_box(box, width, height, base_scale):
    x1, y1, x2, y2 = box
    bw = max(x2 - x1, 1.0)
    bh = max(y2 - y1, 1.0)
    long_side = max(bw, bh)
    if long_side < 35:
        scale = max(float(base_scale), 1.32)
    elif long_side < 70:
        scale = max(float(base_scale), 1.24)
    else:
        scale = max(float(base_scale), 1.16)
    return expand_box(box, width, height, scale, 1)


def crop_upscale_factor(crop_box, target_long_side=256, max_scale=6.0):
    crop_w = max(1.0, crop_box[2] - crop_box[0])
    crop_h = max(1.0, crop_box[3] - crop_box[1])
    scale = float(target_long_side) / max(crop_w, crop_h)
    return max(1.0, min(float(max_scale), scale))


def resize_crop_for_inference(crop, scale):
    if scale <= 1.05:
        return crop
    height, width = crop.shape[:2]
    return cv2.resize(
        crop,
        (max(1, int(round(width * scale))), max(1, int(round(height * scale)))),
        interpolation=cv2.INTER_CUBIC,
    )


def relative_box(box, crop_box):
    return [box[0] - crop_box[0], box[1] - crop_box[1], box[2] - crop_box[0], box[3] - crop_box[1]]


def intersection_ratio(inner, outer):
    ix1 = max(inner[0], outer[0])
    iy1 = max(inner[1], outer[1])
    ix2 = min(inner[2], outer[2])
    iy2 = min(inner[3], outer[3])
    inter = max(0.0, ix2 - ix1) * max(0.0, iy2 - iy1)
    return inter / max(1e-6, box_area(inner))


def load_model(path):
    if YOLO is None:
        raise RuntimeError("ultralytics is not installed")
    if not path:
        return None
    model_path = Path(path)
    if not model_path.exists():
        raise RuntimeError(f"model not found: {path}")
    return YOLO(str(model_path))


def detect_with_models(image, person_model, ppe_models, args, image_source=None):
    height, width = image.shape[:2]
    detections = []
    if person_model is not None:
        model_conf = min(float(args.person_conf), float(args.scene_object_conf), LOW_CONF_MACHINERY_PERSON_CONF)
        scene_labels = {"person", "machinery_vehicle", "excavator", "crane", "forklift", "truck", "ladder", "scaffold", "rebar", "uncapped_rebar", "fire_smoke"}
        source = image_source or image
        for result in person_model.predict(source, conf=model_conf, verbose=False):
            if result.boxes is None:
                continue
            for box in result.boxes:
                cls_id = int(box.cls[0])
                raw = person_model.names.get(cls_id, "")
                label = normalize_label(raw)
                if label not in scene_labels:
                    continue
                confidence = float(box.conf[0])
                safe = clamp_box(box.xyxy[0].tolist(), width, height)
                if not safe:
                    continue
                if label == "person":
                    if confidence < float(args.person_conf):
                        crop = crop_from_box(image, safe)
                        if confidence >= LOW_CONF_MACHINERY_PERSON_CONF and is_likely_machinery_person_crop(crop):
                            detections.append({
                                "label": "excavator",
                                "box": safe,
                                "confidence": confidence,
                                "source": "machinery_like_person_model",
                                "rawLabel": raw,
                            })
                        continue
                if label != "person" and confidence < float(args.scene_object_conf):
                    continue
                detections.append({
                    "label": label,
                    "box": safe,
                    "confidence": confidence,
                    "source": "person_scene_model",
                    "rawLabel": raw,
                })
        refine_imgsz = int(getattr(args, "person_refine_imgsz", 0) or 0)
        if refine_imgsz > 0:
            max_person_area = width * height * 0.18
            for result in person_model.predict(source, conf=float(args.person_conf), imgsz=refine_imgsz, verbose=False):
                if result.boxes is None:
                    continue
                for box in result.boxes:
                    cls_id = int(box.cls[0])
                    raw = person_model.names.get(cls_id, "")
                    label = normalize_label(raw)
                    if label != "person":
                        continue
                    safe = clamp_box(box.xyxy[0].tolist(), width, height)
                    if not safe or box_area(safe) > max_person_area:
                        continue
                    detections.append({
                        "label": "person",
                        "box": safe,
                        "confidence": float(box.conf[0]),
                        "source": "person_scene_model_refine",
                        "rawLabel": raw,
                    })
    return dedupe(detections)


def normalize_edge_detection_boxes(payload, width, height):
    detections = []
    event_type = payload.get("eventType") if isinstance(payload, dict) else None

    def add_box(
        raw_box,
        label="person",
        confidence=0.70,
        source="edge_detection_payload",
        note=None,
        missing_ppe=None,
        risk_reasons=None,
    ):
        if not isinstance(raw_box, list) or len(raw_box) != 4:
            return
        box = raw_box
        if max(box) <= 1.5:
            box = [box[0] * width, box[1] * height, box[2] * width, box[3] * height]
        safe = clamp_box(box, width, height)
        if not safe:
            return
        detection = {
            "label": label,
            "box": safe,
            "confidence": float(confidence),
            "source": source,
            "note": note or "",
            "edgePayloadConfirmed": True,
            "edgePayloadNote": note or "",
            "edgeEventType": event_type or "",
        }
        if missing_ppe:
            detection["edgeMissingPpe"] = list(missing_ppe)
        if risk_reasons:
            detection["edgeRiskReasons"] = list(risk_reasons)
        detections.append(detection)

    def walk(node):
        if isinstance(node, dict):
            position = node.get("position")
            if isinstance(position, dict):
                add_box(
                    position.get("bbox") or position.get("bbox_normalized"),
                    "person",
                    node.get("confidence") or node.get("score") or 0.70,
                    "edge_detection_payload",
                    node.get("stable_id") or node.get("track_id"),
                    node.get("missing_ppe"),
                    node.get("risk_reasons") or node.get("risk_factors"),
                )
            raw_box = node.get("bbox") or node.get("box") or node.get("rect")
            label = normalize_label(node.get("label") or node.get("class") or node.get("type") or "person")
            if label == "person":
                add_box(
                    raw_box,
                    "person",
                    node.get("confidence") or node.get("score") or 0.70,
                    "edge_detection_payload",
                    node.get("stable_id") or node.get("track_id"),
                    node.get("missing_ppe"),
                    node.get("risk_reasons") or node.get("risk_factors"),
                )
            for value in node.values():
                walk(value)
        elif isinstance(node, list):
            for item in node:
                walk(item)

    walk(payload)
    return dedupe(detections, iou_threshold=0.35)


def detect_crop_ppe(crop, crop_origin, ppe_models, args, crop_scale=1.0):
    items = []
    ox, oy = crop_origin
    for model_index, model in enumerate(ppe_models):
        for result in model.predict(crop, conf=args.ppe_conf, imgsz=640, verbose=False):
            if result.boxes is None:
                continue
            for box in result.boxes:
                cls_id = int(box.cls[0])
                raw = model.names.get(cls_id, "")
                label = normalize_label(raw)
                if label not in PPE_DETECTION_LABELS:
                    continue
                x1, y1, x2, y2 = box.xyxy[0].tolist()
                items.append({
                    "label": label,
                    "box": [x1 / crop_scale + ox, y1 / crop_scale + oy, x2 / crop_scale + ox, y2 / crop_scale + oy],
                    "cropBox": [x1, y1, x2, y2],
                    "confidence": float(box.conf[0]),
                    "source": f"person_crop_model_{model_index}",
                    "rawLabel": raw,
                })
    return dedupe(items)


def detect_no_vest_by_upper_color(crop, crop_origin, crop_scale=1.0, person_box=None):
    if crop.size == 0:
        return []

    height, width = crop.shape[:2]
    if height < 20 or width < 12:
        return []

    if person_box:
        ox, oy = crop_origin
        local_person = [
            (person_box[0] - ox) * crop_scale,
            (person_box[1] - oy) * crop_scale,
            (person_box[2] - ox) * crop_scale,
            (person_box[3] - oy) * crop_scale,
        ]
        px1, py1, px2, py2 = clamp_box(local_person, width, height) or [0, 0, width, height]
        person_w = max(1.0, px2 - px1)
        person_h = max(1.0, py2 - py1)
        x1 = int(max(0, px1 + person_w * 0.08))
        x2 = int(min(width, px2 - person_w * 0.08))
        y1 = int(max(0, py1 + person_h * 0.18))
        y2 = int(min(height, py1 + person_h * 0.78))
    else:
        x1 = int(width * 0.03)
        x2 = int(width * 0.97)
        y1 = int(height * 0.05)
        y2 = int(height * 0.90)

    torso = crop[y1:y2, x1:x2]
    if torso.size == 0:
        return []

    hsv = cv2.cvtColor(torso, cv2.COLOR_BGR2HSV)
    yellow = cv2.inRange(hsv, np.array([18, 80, 80]), np.array([42, 255, 255]))
    orange = cv2.inRange(hsv, np.array([5, 80, 80]), np.array([24, 255, 255]))
    lime = cv2.inRange(hsv, np.array([35, 60, 70]), np.array([75, 255, 255]))
    mask = cv2.bitwise_or(cv2.bitwise_or(yellow, orange), lime)
    kernel = np.ones((3, 3), np.uint8)
    mask = cv2.morphologyEx(mask, cv2.MORPH_OPEN, kernel)
    mask = cv2.morphologyEx(mask, cv2.MORPH_CLOSE, kernel)
    colored_pixels = int(cv2.countNonZero(mask))
    total_pixels = max(1, mask.shape[0] * mask.shape[1])
    ratio = colored_pixels / total_pixels
    if colored_pixels < 45 or ratio < 0.005:
        return []

    contours, _ = cv2.findContours(mask, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
    candidates = []
    for candidate in contours:
        bx, by, bw, bh = cv2.boundingRect(candidate)
        center_x = (x1 + bx + bw / 2.0) / max(float(width), 1.0)
        center_y = (y1 + by + bh / 2.0) / max(float(height), 1.0)
        if center_x < 0.12 or center_x > 0.88 or center_y < 0.16 or center_y > 0.88:
            continue
        candidates.append(candidate)
    if not candidates:
        return []
    contour = max(candidates, key=cv2.contourArea)
    if cv2.contourArea(contour) < max(12.0, total_pixels * 0.003):
        return []
    bx, by, bw, bh = cv2.boundingRect(contour)
    min_w = max(bw, int(width * 0.16))
    min_h = max(bh, int(height * 0.22))
    cx = x1 + bx + bw / 2.0
    cy = y1 + by + bh / 2.0
    lx1 = max(0.0, cx - min_w / 2.0)
    ly1 = max(0.0, cy - min_h / 2.0)
    lx2 = min(float(width), cx + min_w / 2.0)
    ly2 = min(float(height), cy + min_h / 2.0)
    ox, oy = crop_origin
    global_box = [
        ox + lx1 / crop_scale,
        oy + ly1 / crop_scale,
        ox + lx2 / crop_scale,
        oy + ly2 / crop_scale,
    ]
    return [{
        "label": "no_vest",
        "box": global_box,
        "cropBox": [lx1, ly1, lx2, ly2],
        "confidence": round(min(0.99, 0.55 + ratio), 4),
        "source": "upper_color_no_vest_rule",
        "rawLabel": "yellow_orange_upper_clothing",
        "colorRatio": round(ratio, 4),
    }]


def detect_excavator_by_color_shape(image):
    if image.size == 0:
        return []
    height, width = image.shape[:2]
    hsv = cv2.cvtColor(image, cv2.COLOR_BGR2HSV)
    yellow_brown = cv2.inRange(hsv, np.array([8, 55, 35]), np.array([38, 255, 180]))
    roi = np.zeros_like(yellow_brown)
    y1, y2 = int(height * 0.20), int(height * 0.82)
    x1, x2 = int(width * 0.15), int(width * 0.95)
    roi[y1:y2, x1:x2] = yellow_brown[y1:y2, x1:x2]
    kernel = cv2.getStructuringElement(cv2.MORPH_RECT, (9, 9))
    mask = cv2.morphologyEx(roi, cv2.MORPH_CLOSE, kernel, iterations=1)
    mask = cv2.dilate(mask, kernel, iterations=1)
    count, _, stats, _ = cv2.connectedComponentsWithStats(mask, 8)
    candidates = []
    for index in range(1, count):
        x, y, box_width, box_height, area = [int(value) for value in stats[index]]
        if area < 5000 or box_width < 55 or box_height < 45:
            continue
        aspect = box_height / max(float(box_width), 1.0)
        fill_ratio = area / max(float(box_width * box_height), 1.0)
        if aspect < 0.70 or aspect > 2.80 or fill_ratio < 0.22:
            continue
        candidates.append((area, fill_ratio, [float(x), float(y), float(x + box_width), float(y + box_height)]))
    if not candidates:
        return []
    candidates.sort(key=lambda item: (item[0], item[1]), reverse=True)
    return [{
        "label": "excavator",
        "box": candidates[0][2],
        "confidence": 0.35,
        "source": "excavator_color_shape_fallback",
    }]


def is_likely_machinery_person_crop(crop):
    if crop.size == 0:
        return False
    height, width = crop.shape[:2]
    if height < 40 or width < 25:
        return False
    hsv = cv2.cvtColor(crop, cv2.COLOR_BGR2HSV)
    brown = cv2.inRange(hsv, np.array([8, 45, 45]), np.array([32, 255, 180]))
    dark = cv2.inRange(hsv, np.array([0, 0, 0]), np.array([179, 255, 55]))
    total_pixels = max(1, height * width)
    brown_ratio = cv2.countNonZero(brown) / total_pixels
    dark_ratio = cv2.countNonZero(dark) / total_pixels
    aspect = height / max(float(width), 1.0)
    return aspect >= 1.35 and brown_ratio >= 0.12 and dark_ratio >= 0.35


def is_likely_static_dark_object_person_crop(crop):
    if crop.size == 0:
        return False
    height, width = crop.shape[:2]
    if height < 45 or width < 25:
        return False
    aspect = height / max(float(width), 1.0)
    if aspect < 1.20 or aspect > 1.95:
        return False

    hsv = cv2.cvtColor(crop, cv2.COLOR_BGR2HSV)
    dark = cv2.inRange(hsv, np.array([0, 0, 0]), np.array([179, 255, 65]))
    yellow = cv2.inRange(hsv, np.array([18, 80, 80]), np.array([42, 255, 255]))
    orange = cv2.inRange(hsv, np.array([5, 80, 80]), np.array([24, 255, 255]))
    lime = cv2.inRange(hsv, np.array([35, 60, 70]), np.array([75, 255, 255]))
    hi_vis = cv2.bitwise_or(cv2.bitwise_or(yellow, orange), lime)

    total_pixels = max(1, height * width)
    dark_ratio = cv2.countNonZero(dark) / total_pixels
    hi_vis_ratio = cv2.countNonZero(hi_vis) / total_pixels
    if dark_ratio < 0.28 or hi_vis_ratio > 0.03:
        return False

    mask = cv2.morphologyEx(dark, cv2.MORPH_CLOSE, np.ones((5, 5), np.uint8))
    contours, _ = cv2.findContours(mask, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
    if not contours:
        return False
    contour = max(contours, key=cv2.contourArea)
    x, y, box_width, box_height = cv2.boundingRect(contour)
    area_ratio = cv2.contourArea(contour) / total_pixels
    touches_top = y <= max(2, int(height * 0.03))
    spans_width = box_width / max(float(width), 1.0) >= 0.78
    return touches_top and spans_width and area_ratio >= 0.20


def is_likely_plastic_sheet_person_crop(crop):
    if crop is None or crop.size == 0:
        return False
    height, width = crop.shape[:2]
    if height < 55 or width < 45:
        return False

    aspect = height / max(float(width), 1.0)
    if aspect < 0.75 or aspect > 1.55:
        return False

    hsv = cv2.cvtColor(crop, cv2.COLOR_BGR2HSV)
    gray = cv2.cvtColor(crop, cv2.COLOR_BGR2GRAY)
    bright = cv2.inRange(gray, 170, 255)
    low_saturation = cv2.inRange(hsv[:, :, 1], 0, 55)
    bright_low_saturation = cv2.bitwise_and(bright, low_saturation)
    dark = cv2.inRange(gray, 0, 75)
    edges = cv2.Canny(gray, 80, 160)

    total_pixels = max(1, height * width)
    bright_low_saturation_ratio = cv2.countNonZero(bright_low_saturation) / total_pixels
    dark_ratio = cv2.countNonZero(dark) / total_pixels
    edge_ratio = cv2.countNonZero(edges) / total_pixels

    return bright_low_saturation_ratio >= 0.12 and edge_ratio >= 0.22 and dark_ratio <= 0.42


def is_likely_hanging_plastic_sheet_person(person, crop, exact_crop=None):
    box = person.get("boxNormalized") or []
    if len(box) != 4:
        return False
    x1, y1, x2, y2 = [float(value) for value in box]
    box_width = max(0.0, x2 - x1)
    box_height = max(0.0, y2 - y1)
    if box_height < 0.55 or box_width < 0.16 or y1 > 0.03:
        return False

    for candidate in (exact_crop, crop):
        if candidate is None or candidate.size == 0:
            continue
        height, width = candidate.shape[:2]
        if height < 180 or width < 120:
            continue
        aspect = height / max(float(width), 1.0)
        if aspect < 1.45 or aspect > 2.35:
            continue

        hsv = cv2.cvtColor(candidate, cv2.COLOR_BGR2HSV)
        gray = cv2.cvtColor(candidate, cv2.COLOR_BGR2GRAY)
        bright = cv2.inRange(gray, 165, 255)
        low_saturation = cv2.inRange(hsv[:, :, 1], 0, 60)
        bright_low_saturation = cv2.bitwise_and(bright, low_saturation)
        dark = cv2.inRange(gray, 0, 75)

        total_pixels = max(1, height * width)
        bright_low_saturation_ratio = cv2.countNonZero(bright_low_saturation) / total_pixels
        dark_ratio = cv2.countNonZero(dark) / total_pixels
        if bright_low_saturation_ratio >= 0.16 and dark_ratio <= 0.30:
            return True
    return False


def has_model_ppe_detection(ppe_items):
    return any(str(item.get("source") or "").startswith("person_crop_model") for item in ppe_items or [])


def resolve_conflicting_ppe_labels(ppe_items):
    has_vest = any(item.get("label") == "vest" for item in ppe_items or [])
    if not has_vest:
        return ppe_items
    return [item for item in ppe_items if item.get("label") != "no_vest"]


def box_center_inside(box, outer):
    cx = (box[0] + box[2]) / 2.0
    cy = (box[1] + box[3]) / 2.0
    return outer[0] <= cx <= outer[2] and outer[1] <= cy <= outer[3]


def expanded_for_membership(box, scale=1.15):
    x1, y1, x2, y2 = box
    cx = (x1 + x2) / 2.0
    cy = (y1 + y2) / 2.0
    width = (x2 - x1) * scale
    height = (y2 - y1) * scale
    return [cx - width / 2.0, cy - height / 2.0, cx + width / 2.0, cy + height / 2.0]


def is_ppe_box_for_person(item, person, crop_box, all_persons=None):
    person_box = person["box"]
    if intersection_ratio(item["box"], crop_box) < 0.80:
        return False
    membership_box = expanded_for_membership(person_box, 1.18)
    if not (box_center_inside(item["box"], membership_box) or intersection_ratio(item["box"], membership_box) >= 0.45):
        return False

    if item.get("label") == "no_vest" and item.get("source") == "upper_color_no_vest_rule":
        person_bottom = person_box[3]
        person_height = max(1.0, person_box[3] - person_box[1])
        for other in all_persons or []:
            if other is person or other.get("id") == person.get("id"):
                continue
            other_box = other["box"]
            overlaps_other = box_center_inside(item["box"], other_box) or intersection_ratio(item["box"], other_box) >= 0.65
            foreground_other = other_box[3] > person_bottom + max(8.0, person_height * 0.10)
            if overlaps_other and foreground_other:
                return False

    return True


def is_likely_partial_edge_fragment_person(person):
    box = person.get("boxNormalized") or []
    if len(box) != 4:
        return False
    x1, y1, x2, y2 = [float(value) for value in box]
    box_width = max(0.0, x2 - x1)
    box_height = max(0.0, y2 - y1)
    top_fragment = y1 <= 0.035 and (box_height <= 0.14 or box_width <= 0.07)
    left_fragment = x1 <= 0.005 and box_width <= 0.035
    tiny_vertical_fragment = box_width <= 0.026 and box_height <= 0.18
    return top_fragment or left_fragment or tiny_vertical_fragment


def is_likely_scene_fragment_person_crop(crop):
    if crop.size == 0:
        return False
    height, width = crop.shape[:2]
    if height < 45 or width < 45:
        return False
    aspect = height / max(float(width), 1.0)
    hsv = cv2.cvtColor(crop, cv2.COLOR_BGR2HSV)
    gray = cv2.cvtColor(crop, cv2.COLOR_BGR2GRAY)
    yellow = cv2.inRange(hsv, np.array([18, 80, 80]), np.array([42, 255, 255]))
    orange = cv2.inRange(hsv, np.array([5, 80, 80]), np.array([24, 255, 255]))
    lime = cv2.inRange(hsv, np.array([35, 60, 70]), np.array([75, 255, 255]))
    hi_vis = cv2.bitwise_or(cv2.bitwise_or(yellow, orange), lime)
    edges = cv2.Canny(gray, 80, 160)
    total_pixels = max(1, height * width)
    hi_vis_ratio = cv2.countNonZero(hi_vis) / total_pixels
    edge_ratio = cv2.countNonZero(edges) / total_pixels
    return hi_vis_ratio < 0.01 and edge_ratio >= 0.18 and aspect <= 1.25


def should_exclude_person_candidate(person, ppe_items, crop, exact_crop=None):
    source = str(person.get("source") or "")
    confidence = float(person.get("confidence") or 0)
    edge_confirmed = bool(person.get("edgePayloadConfirmed") or source == "edge_detection_payload")
    x1, y1, x2, y2 = person["box"]
    width = max(1.0, x2 - x1)
    height = max(1.0, y2 - y1)
    aspect = height / width
    if source == "edge_detection_payload" and aspect < 1.20 and width >= 60 and height >= 60:
        return True
    exact_machinery_crop = exact_crop is not None and exact_crop.size > 0 and is_likely_machinery_person_crop(exact_crop)
    machinery_crop = exact_machinery_crop or is_likely_machinery_person_crop(crop)
    if source == "edge_detection_payload" and machinery_crop:
        return not has_model_ppe_detection(ppe_items)
    if edge_confirmed and machinery_crop and not has_model_ppe_detection(ppe_items):
        return True
    if edge_confirmed and not has_model_ppe_detection(ppe_items):
        if is_likely_static_dark_object_person_crop(exact_crop) or is_likely_static_dark_object_person_crop(crop):
            return True
        return False
    if not has_model_ppe_detection(ppe_items):
        if (
            source == "person_scene_model"
            and confidence < 0.70
            and (
                is_likely_plastic_sheet_person_crop(exact_crop)
                or is_likely_plastic_sheet_person_crop(crop)
            )
        ):
            return True
        if (
            source == "person_scene_model"
            and confidence < 0.70
            and is_likely_hanging_plastic_sheet_person(person, crop, exact_crop)
        ):
            return True
        if is_likely_partial_edge_fragment_person(person) or is_likely_scene_fragment_person_crop(crop):
            return True
    if source != "person_scene_model" or confidence >= 0.12:
        return False
    if machinery_crop:
        return True
    if ppe_items:
        return False
    return aspect >= 2.1 or confidence < 0.08


def image_to_data_url(image, max_width=1280):
    height, width = image.shape[:2]
    if width > max_width:
        scale = max_width / width
        image = cv2.resize(image, (int(width * scale), int(height * scale)), interpolation=cv2.INTER_AREA)
    ok, encoded = cv2.imencode(".jpg", image, [int(cv2.IMWRITE_JPEG_QUALITY), 78])
    if not ok:
        raise RuntimeError("failed to encode image")
    return "data:image/jpeg;base64," + base64.b64encode(encoded.tobytes()).decode("ascii")


def openai_high_work(image, persons, required_ppe, model):
    if not os.getenv("OPENAI_API_KEY"):
        return {}
    try:
        from openai import OpenAI
        client = OpenAI(api_key=os.getenv("OPENAI_API_KEY"), base_url=os.getenv("OPENAI_BASE_URL") or None)
        prompt = {
            "task": "construction_site_event_risk_review",
            "instructions": [
                "Return raw JSON only.",
                "Identify people who are at height, climbing, suspended, on/near scaffold, on ladder, or in fall-risk areas.",
                "Use person ids from the provided list. Do not invent people.",
                "For each risk person include reason, height_risk, and missing_ppe if visible.",
            ],
            "required_ppe": required_ppe,
            "persons": [{"person_id": p["id"], "box": p["boxNormalized"]} for p in persons],
            "format": {
                "persons_at_height": [
                    {"person_id": "person_001", "height_risk": "on ladder", "reason": "standing on ladder", "missing_ppe": ["helmet"]}
                ],
                "scene_risk": "short summary",
            },
        }
        response = client.responses.create(
            model=model,
            input=[{
                "role": "user",
                "content": [
                    {"type": "input_text", "text": json.dumps(prompt, ensure_ascii=False)},
                    {"type": "input_image", "image_url": image_to_data_url(image)},
                ],
            }],
            temperature=0,
        )
        text = response.output_text.strip().removeprefix("```json").removeprefix("```").removesuffix("```").strip()
        return json.loads(text)
    except Exception as exc:
        return {"error": str(exc)}


def openai_scene_annotation(image, model):
    if not os.getenv("OPENAI_API_KEY"):
        return {}
    try:
        from openai import OpenAI
        client = OpenAI(api_key=os.getenv("OPENAI_API_KEY"), base_url=os.getenv("OPENAI_BASE_URL") or None)
        prompt = {
            "task": "construction_site_panorama_detection",
            "instructions": [
                "Return raw JSON only.",
                "Use normalized xyxy boxes in [0,1].",
                "Detect visible people and large construction-site objects: machinery, vehicles, dangerous equipment, ladders, scaffolds, rebar, exposed/uncapped rebar, fire or smoke.",
                "Do not identify PPE items in the panorama. PPE is reviewed only on person crops.",
                "Do not invent boxes.",
            ],
            "allowed_labels": CLASSES,
            "format": {
                "boxes": [
                    {"label": "person", "box": [0.1, 0.2, 0.3, 0.9], "confidence": 0.9, "note": "worker"}
                ],
                "scene_summary": "short summary",
            },
        }
        response = client.responses.create(
            model=model,
            input=[{
                "role": "user",
                "content": [
                    {"type": "input_text", "text": json.dumps(prompt, ensure_ascii=False)},
                    {"type": "input_image", "image_url": image_to_data_url(image)},
                ],
            }],
            temperature=0,
        )
        text = response.output_text.strip().removeprefix("```json").removeprefix("```").removesuffix("```").strip()
        return json.loads(text)
    except Exception as exc:
        return {"error": str(exc)}


def normalize_openai_boxes(result, width, height):
    detections = []
    scene_labels = {"person", "machinery_vehicle", "excavator", "crane", "forklift", "truck", "ladder", "scaffold", "rebar", "uncapped_rebar", "fire_smoke"}
    for item in result.get("boxes") or []:
        label = normalize_label(item.get("label"))
        raw_box = item.get("box") or []
        if label not in scene_labels or len(raw_box) != 4:
            continue
        if max(raw_box) <= 1.5:
            raw_box = [raw_box[0] * width, raw_box[1] * height, raw_box[2] * width, raw_box[3] * height]
        box = clamp_box(raw_box, width, height)
        if not box:
            continue
        detections.append({
            "label": label,
            "box": box,
            "confidence": float(item.get("confidence") or 0.70),
            "source": "openai_scene",
            "note": item.get("note") or "",
        })
    return detections


def draw_boxes(image, detections, subjects=None):
    colors = {
        "person": (40, 200, 40),
        "helmet": (255, 190, 0),
        "vest": (0, 220, 255),
        "goggles": (255, 128, 0),
        "gloves": (255, 0, 255),
        "boots": (180, 120, 40),
        "machinery_vehicle": (0, 0, 255),
        "no_helmet": (0, 0, 255),
        "no_goggle": (0, 0, 255),
        "no_gloves": (0, 0, 255),
        "no_boots": (0, 0, 255),
        "no_vest": (0, 0, 255),
        "dangerous_equipment": (0, 0, 255),
        "ladder": (0, 128, 255),
        "scaffold": (160, 64, 255),
        "rebar": (80, 80, 255),
        "fire_smoke": (0, 80, 255),
    }
    output = image.copy()
    for det in detections:
        x1, y1, x2, y2 = [int(round(v)) for v in det["box"]]
        color = colors.get(det["label"], (255, 255, 255))
        cv2.rectangle(output, (x1, y1), (x2, y2), color, 2)
        cv2.putText(output, det["label"], (x1, max(18, y1 - 5)), cv2.FONT_HERSHEY_SIMPLEX, 0.5, color, 1)
    for subject in subjects or []:
        box = subject.get("box")
        if not box:
            continue
        x1, y1, x2, y2 = [int(round(v)) for v in box]
        color = (0, 0, 255) if subject["risk"]["isRisk"] else (40, 200, 40)
        cv2.rectangle(output, (x1, y1), (x2, y2), color, 3)
        cv2.putText(output, subject["id"], (x1, min(output.shape[0] - 8, y2 + 18)), cv2.FONT_HERSHEY_SIMPLEX, 0.55, color, 2)
    return output


def parse_json_file(path):
    if not path:
        return None
    try:
        return json.loads(Path(path).read_text(encoding="utf-8"))
    except Exception:
        return None


def main():
    args = parse_args()
    image_path = Path(args.image_path)
    output_dir = Path(args.output_dir)
    output_dir.mkdir(parents=True, exist_ok=True)
    crops_dir = output_dir / "person_crops"
    previews_dir = output_dir / "previews"
    labels_dir = output_dir / "labels"
    for directory in (crops_dir, previews_dir, labels_dir):
        directory.mkdir(parents=True, exist_ok=True)

    image = cv2.imread(str(image_path))
    if image is None:
        raise RuntimeError(f"cannot read image: {image_path}")
    height, width = image.shape[:2]

    person_model = load_model(args.person_model) if args.person_model else None
    ppe_models = [load_model(path) for path in args.ppe_model if path]
    edge_detection = parse_json_file(args.detection_json)
    detections = detect_with_models(image, person_model, ppe_models, args, str(image_path))
    if edge_detection:
        detections = dedupe(detections + normalize_edge_detection_boxes(edge_detection, width, height), iou_threshold=0.35)
    excavator_fallbacks = detect_excavator_by_color_shape(image)
    if excavator_fallbacks:
        detections = dedupe(detections + excavator_fallbacks, iou_threshold=0.35)
    openai_scene = {}
    if args.use_openai:
        openai_scene = openai_scene_annotation(image, args.openai_model)
        if args.use_openai_scene_boxes:
            detections = dedupe(detections + normalize_openai_boxes(openai_scene, width, height))
    persons = []
    scene_objects = []
    for det in detections:
        if det["label"] == "person":
            persons.append(det)
        elif det["label"] in {"machinery_vehicle", "excavator", "crane", "forklift", "truck", "ladder", "scaffold", "rebar", "uncapped_rebar", "fire_smoke"}:
            scene_objects.append(det)

    required_ppe = [item.strip() for item in args.required_ppe.split(",") if item.strip()] or REQUIRED_DEFAULT
    person_prompts = []
    for index, person in enumerate(persons, start=1):
        person_id = f"person_{index:03}"
        person["id"] = person_id
        person["boxNormalized"] = [
            round(person["box"][0] / width, 6),
            round(person["box"][1] / height, 6),
            round(person["box"][2] / width, 6),
            round(person["box"][3] / height, 6),
        ]
        person_prompts.append(person)

    openai_high_work = {}
    if args.use_openai and persons:
        openai_high_work = globals()["openai_high_work"](image, person_prompts, required_ppe, args.openai_model)
    high_work_marks = {}
    for item in openai_high_work.get("persons_at_height", []) if isinstance(openai_high_work, dict) else []:
        person_id = str(item.get("person_id") or "")
        if person_id:
            high_work_marks[person_id] = item

    subject_results = []
    all_ppe_items = []
    for person in persons:
        crop_box = adaptive_person_crop_box(person["box"], width, height, args.person_crop_scale)
        cx1, cy1, cx2, cy2 = [int(round(value)) for value in crop_box]
        raw_crop = image[cy1:cy2, cx1:cx2]
        if raw_crop.size == 0:
            continue
        px1, py1, px2, py2 = [int(round(value)) for value in person["box"]]
        exact_crop = image[py1:py2, px1:px2]
        inference_scale = crop_upscale_factor(
            [cx1, cy1, cx2, cy2],
            max(192, int(args.person_crop_min_side) * 2),
        )
        crop = resize_crop_for_inference(raw_crop, inference_scale)
        crop_name = f"{person['id']}.jpg"
        crop_path = crops_dir / crop_name
        cv2.imwrite(str(crop_path), crop)
        saved_crop = cv2.imread(str(crop_path))
        color_crop = saved_crop if saved_crop is not None else crop

        ppe_items = detect_crop_ppe(crop, (cx1, cy1), ppe_models, args, inference_scale)
        color_no_vest = detect_no_vest_by_upper_color(color_crop, (cx1, cy1), inference_scale, person["box"])
        if color_no_vest and not any(item["label"] == "no_vest" for item in ppe_items):
            ppe_items.extend(color_no_vest)
        ppe_items = resolve_conflicting_ppe_labels(ppe_items)
        ppe_items = dedupe(ppe_items)
        ppe_items = [item for item in ppe_items if is_ppe_box_for_person(item, person, [cx1, cy1, cx2, cy2], persons)]
        if should_exclude_person_candidate(person, ppe_items, raw_crop, exact_crop):
            continue
        all_ppe_items.extend(ppe_items)
        person["ppe"] = {}
        for item in ppe_items:
            current = person["ppe"].get(item["label"])
            if current is None or item["confidence"] > current["confidence"]:
                person["ppe"][item["label"]] = item

        risk = risk_rules.assess_person_risk(
            person,
            scene_objects,
            required_ppe,
            width,
            height,
            high_work_marks,
            parse_risk_zones(args.risk_zones_json),
        )
        crop_h, crop_w = crop.shape[:2]
        crop_labels = []
        for item in ppe_items:
            rel = clamp_box(item.get("cropBox"), crop_w, crop_h)
            if rel:
                crop_labels.append(yolo_line(item["label"], rel, crop_w, crop_h))

        label_path = labels_dir / f"{person['id']}.txt"
        label_path.write_text(yolo_text(crop_labels) + ("\n" if crop_labels else ""), encoding="utf-8")

        crop_preview = draw_boxes(crop, [
            {"label": row["label"], "box": [
                (row["xCenter"] - row["width"] / 2) * crop_w,
                (row["yCenter"] - row["height"] / 2) * crop_h,
                (row["xCenter"] + row["width"] / 2) * crop_w,
                (row["yCenter"] + row["height"] / 2) * crop_h,
            ]}
            for row in crop_labels
        ])
        preview_name = f"{person['id']}_preview.jpg"
        cv2.imwrite(str(previews_dir / preview_name), crop_preview)

        ppe_status = {
            "required": required_ppe,
            "present": sorted(person["ppe"].keys()),
            "missing": risk["missingPpe"],
            "complete": risk["ppeComplete"],
            "detectionAttempted": bool(ppe_models),
            "detectedBoxCount": len(ppe_items),
        }
        subject_results.append({
            "subjectKey": person["id"],
            "subjectType": "Person",
            "trackingLabel": f"Person {person['id'].split('_')[-1]}",
            "cropImageUrl": f"{args.public_url_prefix}/person_crops/{crop_name}",
            "previewImageUrl": f"{args.public_url_prefix}/previews/{preview_name}",
            "boundingBox": xyxy_to_box_object(person["box"], "person", person.get("confidence")),
            "ppeBoxes": [
                {
                    "label": item["label"],
                    "globalBox": xyxy_to_box_object(item["box"], item["label"], item.get("confidence")),
                    "cropBox": xyxy_to_box_object(clamp_box(item.get("cropBox"), crop_w, crop_h) or [0, 0, 0, 0], item["label"], item.get("confidence")),
                    "source": item.get("source"),
                }
                for item in ppe_items
            ],
            "ppeStatus": ppe_status,
            "isRisk": risk["isRisk"],
            "riskCategory": risk["riskCategory"],
            "riskSeverity": risk["riskSeverity"],
            "riskReason": risk["riskReason"],
            "analysisJson": {
                "cropBox": xyxy_to_box_object([cx1, cy1, cx2, cy2]),
                "cropScale": round(float(inference_scale), 4),
                "cropImageWidth": crop_w,
                "cropImageHeight": crop_h,
                "cropLabelFile": f"{args.public_url_prefix}/labels/{person['id']}.txt",
                "risk": risk,
            },
            "trainingLabels": crop_labels,
            "_box": person["box"],
            "_risk": risk,
        })

    kept_person_boxes = [subject["_box"] for subject in subject_results]
    panorama_detections = dedupe([
        det for det in detections
        if det["label"] != "person" or any(box_overlap_ratio(det["box"], box) >= 0.80 for box in kept_person_boxes)
    ])
    panorama_boxes = [
        {
            "classId": CLASS_ID[class_key(det["label"])],
            "label": det["label"],
            **xyxy_to_box_object(det["box"], None, det.get("confidence")),
            "source": det.get("source"),
        }
        for det in panorama_detections
        if class_key(det["label"]) in CLASS_ID
    ]
    panorama_annotation = {
        "imageUrl": args.image_url or str(image_path),
        "imageWidth": width,
        "imageHeight": height,
        "classes": [{"id": index, "name": name} for index, name in enumerate(CLASSES)],
        "boxes": panorama_boxes,
    }

    preview = draw_boxes(image, panorama_detections, [{"id": s["subjectKey"], "box": s["_box"], "risk": s["_risk"]} for s in subject_results])
    cv2.imwrite(str(previews_dir / "panorama_preview.jpg"), preview)

    people_count = len(subject_results)
    risk_people = sum(1 for item in subject_results if item["isRisk"])
    ppe_ok = sum(1 for item in subject_results if item["ppeStatus"]["complete"])
    event_risk = risk_rules.classify_event_risk(
        people_count,
        scene_objects,
        [item["_risk"] for item in subject_results],
    )
    risk_people = event_risk["riskPersonCount"]
    risk_category = event_risk["riskCategory"]
    risk_severity = event_risk["riskSeverity"]
    ppe_rate = round(ppe_ok * 100.0 / people_count, 2) if people_count else None
    summary = event_risk["summary"]

    training = {
        "eventId": args.event_id,
        "projectId": args.project_id,
        "deviceCode": args.device_code,
        "image": {
            "url": args.image_url,
            "file": str(image_path),
            "width": width,
            "height": height,
            "previewUrl": f"{args.public_url_prefix}/previews/panorama_preview.jpg",
        },
        "split": split_for_event(args.event_id, args.val_ratio),
        "panoramaAnnotation": panorama_annotation,
        "personCrops": [
            {key: value for key, value in subject.items() if not key.startswith("_")}
            for subject in subject_results
        ],
        "analysis": {
            "peopleCount": people_count,
            "machineryVehicleCount": sum(1 for item in scene_objects if item["label"] in {"machinery_vehicle", "excavator", "crane", "forklift", "truck"}),
            "toolCount": sum(1 for item in scene_objects if item["label"] in {"ladder", "scaffold", "rebar", "uncapped_rebar"}),
            "ppeCompliantPeopleCount": ppe_ok,
            "riskPersonCount": risk_people,
            "ppeComplianceRate": ppe_rate,
            "riskCategory": risk_category,
            "riskSeverity": risk_severity,
            "summary": summary,
            "status": event_risk["status"],
        },
        "openAIHighWork": openai_high_work,
        "openAIScene": openai_scene,
        "edgeDetectionJson": edge_detection,
    }
    (output_dir / "training_event.json").write_text(json.dumps(training, ensure_ascii=False, indent=2), encoding="utf-8")

    result_subjects = []
    for subject in subject_results:
        clean = {key: value for key, value in subject.items() if not key.startswith("_") and key != "trainingLabels"}
        result_subjects.append(clean)

    result = {
        "analysis": training["analysis"],
        "panoramaAnnotation": panorama_annotation,
        "subjects": result_subjects,
        "trainingJsonUrl": f"{args.public_url_prefix}/training_event.json",
        "analysisSource": "console_event_auto_analysis",
        "analyzedAtUtc": utc_now(),
    }
    Path(args.output_json).write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps({"eventId": args.event_id, "subjects": len(result_subjects), "riskPeople": risk_people}, ensure_ascii=False))


def build_summary(people_count, risk_people, major_risks, scene_objects):
    scene_labels = sorted({item["label"] for item in scene_objects})
    if major_risks:
        return f"{len(major_risks)} person(s) are in a high-work/fall-risk area with missing PPE. Scene objects: {', '.join(scene_labels) or 'none'}."
    if risk_people:
        return f"{risk_people} risk person(s) detected. Scene objects: {', '.join(scene_labels) or 'none'}."
    return f"{people_count} person(s) detected. No major risk was confirmed. Scene objects: {', '.join(scene_labels) or 'none'}."


def split_for_event(event_id, val_ratio):
    digest = hashlib.sha1(str(event_id).encode("utf-8")).hexdigest()
    bucket = int(digest[:8], 16) / 0xFFFFFFFF
    return "val" if bucket < val_ratio else "train"


def utc_now():
    from datetime import datetime, timezone
    return datetime.now(timezone.utc).replace(microsecond=0).isoformat().replace("+00:00", "Z")


def parse_risk_zones(value):
    if not value:
        return []
    try:
        parsed = json.loads(value)
        return parsed if isinstance(parsed, list) else []
    except Exception:
        return []


def parse_args():
    parser = argparse.ArgumentParser(description="Analyze one Console edge risk event and produce person PPE slices plus training JSON.")
    parser.add_argument("--event-id", required=True)
    parser.add_argument("--project-id", required=True)
    parser.add_argument("--device-code", required=True)
    parser.add_argument("--image-path", required=True)
    parser.add_argument("--image-url", default="")
    parser.add_argument("--output-dir", required=True)
    parser.add_argument("--public-url-prefix", required=True)
    parser.add_argument("--output-json", required=True)
    parser.add_argument("--detection-json", default="")
    parser.add_argument("--person-model", default=os.getenv("PERSON_MODEL", "/opt/sentribee-edge-ai/yolo11n.pt"))
    parser.add_argument("--ppe-model", action="append", default=[])
    parser.add_argument("--person-conf", type=float, default=0.35)
    parser.add_argument("--person-refine-imgsz", type=int, default=int(os.getenv("PERSON_REFINE_IMGSZ", "1280")))
    parser.add_argument("--ppe-conf", type=float, default=0.20)
    parser.add_argument("--scene-object-conf", type=float, default=0.25)
    parser.add_argument("--val-ratio", type=float, default=0.20)
    parser.add_argument("--person-crop-scale", type=float, default=1.10)
    parser.add_argument("--person-crop-min-side", type=int, default=96)
    parser.add_argument("--required-ppe", default="helmet,vest")
    parser.add_argument("--risk-zones-json", default="")
    parser.add_argument("--use-openai", action="store_true")
    parser.add_argument("--use-openai-scene-boxes", action="store_true")
    parser.add_argument("--openai-model", default=os.getenv("OPENAI_MODEL", "gpt-4.1-mini"))
    args = parser.parse_args()
    if not args.ppe_model:
        args.ppe_model = [
            os.getenv("PPE_MODEL", "/opt/sentribee-edge-ai/best.pt"),
            os.getenv("PPE_MODEL_2", "/opt/sentribee-edge-ai/best2.pt"),
        ]
    return args


if __name__ == "__main__":
    try:
        main()
    except Exception as exc:
        print(f"[console_event_analyzer] {exc}", file=sys.stderr)
        sys.exit(1)
