"""Independently written conditional-flow training on an explicitly procedural example.

Math references: gouruiyu/ControlOperators train.py:770-886, networks.py:16-28,
controller.py:924-964. This script trains its own declared ELU MLP representation.
"""
import argparse
import json
from pathlib import Path
import numpy as np
import torch


def motion_example(config):
    rng = np.random.default_rng(config["dataSeed"])
    previous, following, controls = [], [], []
    dt = config["frameTime"]
    for _ in range(config["sequences"]):
        phase = rng.uniform(-np.pi, np.pi)
        state = np.array([np.sin(phase), np.cos(phase), *rng.uniform(-1, 1, 4)], np.float32)
        for frame in range(config["frames"]):
            if frame % 8 == 0:
                request = rng.uniform(-1, 1, 4).astype(np.float32)
            phase += dt * (1.5 + .3 * abs(float(state[2])))
            next_state = np.array([np.sin(phase), np.cos(phase),
                                   *(.6 * state[2:4] + .4 * request[:2]),
                                   *(.7 * state[4:6] + .3 * request[2:])], np.float32)
            previous.append(state)
            following.append(next_state)
            controls.append([request[0], request[1], 0, request[2], request[3], 0])
            state = next_state
    return np.asarray(previous), np.asarray(following), np.asarray(controls, np.float32)


def pose_features(latent):
    phase_sin, phase_cos, vx, vy, ax, ay = latent.T
    zero = np.zeros_like(vx)
    result = np.zeros((len(latent), 32), np.float32)
    result[:, :3] = np.stack((.4 * vx, .1 * vy, zero), axis=1)
    result[:, 3:6] = np.stack((zero, zero, .2 * ax), axis=1)
    result[:, 6:9] = np.stack((.1 * vx, .05 * phase_sin, zero), axis=1)
    for bone, angle in enumerate((.6 * ax + .1 * phase_sin, .5 * ay - .1 * phase_cos)):
        result[:, 9 + 6 * bone:15 + 6 * bone] = np.stack(
            (np.cos(angle), -np.sin(angle), np.sin(angle), np.cos(angle), zero, zero), axis=1)
    result[:, 21:24] = np.stack((zero, .08 * phase_cos, zero), axis=1)
    result[:, 24:27] = np.stack((zero, zero, .16 * phase_cos), axis=1)
    result[:, 27:30] = np.stack((zero, zero, .16 * phase_sin), axis=1)
    result[:, 30:32] = np.stack((.5 + .45 * phase_sin, .5 - .45 * phase_sin), axis=1)
    return result


def mlp(widths):
    layers = []
    for index, (source, target) in enumerate(zip(widths, widths[1:])):
        layers.append(torch.nn.Linear(source, target))
        if index < len(widths) - 2:
            layers.append(torch.nn.ELU())
    return torch.nn.Sequential(*layers)


def network_data(network, widths):
    linear = [layer for layer in network if isinstance(layer, torch.nn.Linear)]
    return {"widths": widths,
            "weights": [layer.weight.detach().numpy().reshape(-1).tolist() for layer in linear],
            "biases": [layer.bias.detach().numpy().tolist() for layer in linear]}


def train(config):
    torch.set_num_threads(config["threads"])
    torch.set_num_interop_threads(1)
    torch.use_deterministic_algorithms(True)
    torch.manual_seed(config["trainingSeed"])
    before, after, controls = motion_example(config)
    poses = pose_features(after)
    all_latents = np.concatenate((before, after))
    z_offset = all_latents.mean(axis=0)
    z_scale = np.full(6, all_latents.std(axis=0).mean(), np.float32)
    z_before, z_after = (before - z_offset) / z_scale, (after - z_offset) / z_scale
    z_all = (all_latents - z_offset) / z_scale
    z_dist = z_all.std(axis=0)
    x_offset = poses.mean(axis=0)
    x_scale = np.full(32, poses.std(axis=0).mean(), np.float32)
    x_normal = (poses - x_offset) / x_scale
    x_dist = x_normal.std(axis=0)
    decoder, flow = mlp(config["decoderWidths"]), mlp(config["flowWidths"])
    x, zd, previous, following, control = [torch.from_numpy(value) for value in
                                          (x_normal, after, z_before, z_after, controls)]
    zdev, xdev = torch.from_numpy(z_dist), torch.from_numpy(x_dist)
    count, batch = len(after), config["batchSize"]
    optimizer = torch.optim.AdamW(decoder.parameters(), lr=config["learningRate"])
    for step in range(config["decoderIterations"]):
        indices = torch.randint(count, (batch,))
        prediction = decoder(zd[indices]) * xdev
        loss = ((prediction - x[indices]) ** 2).mean()
        optimizer.zero_grad(set_to_none=True)
        loss.backward()
        optimizer.step()
    print(f"decoder_loss={loss.item():.8f}", flush=True)
    optimizer = torch.optim.AdamW(flow.parameters(), lr=config["learningRate"])
    for step in range(config["flowIterations"]):
        indices = torch.randint(count, (batch,))
        start = torch.randn(batch, 6) * zdev
        time = torch.rand(batch, 1)
        history = previous[indices] + config["previousNoise"] * zdev * torch.rand(batch, 1) * torch.randn(batch, 6)
        transported = (1 - time) * start + time * following[indices]
        velocity = flow(torch.cat((transported, history, control[indices], time), dim=1)) * zdev
        loss = ((velocity - (following[indices] - start)) ** 2).mean()
        optimizer.zero_grad(set_to_none=True)
        loss.backward()
        optimizer.step()
        optimizer.param_groups[0]["lr"] = config["learningRate"] * (1 - .9 * (step + 1) / config["flowIterations"])
    print(f"flow_loss={loss.item():.8f}", flush=True)
    with torch.no_grad():
        reconstruction = (decoder(zd) * xdev).numpy()
    print(f"decoder_pose_mae={np.abs((reconstruction - x_normal) * x_scale).mean():.8f}", flush=True)
    return {
        "version": 1, "boneCount": 3, "frameTime": config["frameTime"],
        "steps": config["integrationSteps"], "controls": config["controls"],
        "controlOffset": [0] * 6, "controlScale": [1] * 6,
        "latentOffset": z_offset.tolist(), "latentScale": z_scale.tolist(),
        "latentDeviation": z_dist.tolist(), "latentMinimum": z_all.min(axis=0).tolist(),
        "latentMaximum": z_all.max(axis=0).tolist(),
        "initialHistory": ((np.array([0, 1, 0, 0, 0, 0], np.float32) - z_offset) / z_scale).tolist(),
        "poseOffset": x_offset.tolist(), "poseScale": x_scale.tolist(), "poseDeviation": x_dist.tolist(),
        "flow": network_data(flow, config["flowWidths"]),
        "decoder": network_data(decoder, config["decoderWidths"])}


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--config", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    config = json.loads(args.config.read_text(encoding="utf-8"))
    print(f"torch={torch.__version__} numpy={np.__version__} samples={config['sequences'] * config['frames']}", flush=True)
    model = train(config)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(model, separators=(",", ":")), encoding="utf-8")
    print(f"model={args.output} topology={config['flowWidths']} decoder={config['decoderWidths']}", flush=True)
