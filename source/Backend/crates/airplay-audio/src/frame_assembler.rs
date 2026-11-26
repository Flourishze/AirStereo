use airplay_core::AudioFormat;

/// Accumulates interleaved f32 stereo frames, slices exact 352-frame packets, and converts to i16.
pub struct FrameAssembler {
    frames_per_packet: usize,
    pending: Vec<f32>,
}

impl FrameAssembler {
    pub fn new(format: AudioFormat) -> Self {
        let fpp = format.frames_per_packet as usize;
        Self {
            frames_per_packet: fpp,
            pending: Vec::with_capacity(fpp * 4),
        }
    }

    pub fn push_interleaved_f32(&mut self, samples: &[f32]) -> Vec<Vec<i16>> {
        self.pending.extend_from_slice(samples);
        let needed = self.frames_per_packet * 2;
        let mut packets = Vec::new();
        while self.pending.len() >= needed {
            let chunk: Vec<i16> = self
                .pending
                .drain(..needed)
                .map(|s| (s.clamp(-1.0, 1.0) * 32767.0).round() as i16)
                .collect();
            packets.push(chunk);
        }
        packets
    }

    pub fn clear(&mut self) {
        self.pending.clear();
    }
}
