using Xunit;

namespace IndexNode.Tests;

public class PitchTests
{
    // right eye, left eye, nose, mouth corners (x,y each); eye line y=50, mouth line y=100
    static float[] Kps(float noseY, float roll = 0) => [40, 50, 80, 50 + roll, 60, noseY, 50, 100, 70, 100];

    [Fact]
    public void A_frontal_face_has_pitch_near_zero_and_looking_down_or_up_moves_it_either_way()
    {
        Assert.InRange(FaceMath.Pitch(Kps(77.5f)), -0.01, 0.01);
        Assert.True(FaceMath.Pitch(Kps(91)) > 0.25);    // nose pushed toward the mouth: looking down
        Assert.True(FaceMath.Pitch(Kps(60)) < -0.25);   // nose near the eyes: head tipped back
        Assert.Equal(9, FaceMath.Pitch([40, 50, 80, 50, 60, 70, 60, 50, 60, 50]));   // degenerate landmarks
    }

    [Fact]
    public void Pitch_is_measured_along_the_faces_own_vertical_so_a_tilted_head_is_not_a_nod()
    {
        // the whole face rotated 20 degrees about the origin: same ratio
        float[] Rot(float[] k) { var o = new float[10]; double c = Math.Cos(0.35), s = Math.Sin(0.35); for (int i = 0; i < 5; i++) { o[2 * i] = (float)(k[2 * i] * c - k[2 * i + 1] * s); o[2 * i + 1] = (float)(k[2 * i] * s + k[2 * i + 1] * c); } return o; }
        Assert.Equal(FaceMath.Pitch(Kps(77.5f)), FaceMath.Pitch(Rot(Kps(77.5f))), 3);
        Assert.Equal(FaceMath.Pitch(Kps(91)), FaceMath.Pitch(Rot(Kps(91))), 3);
    }

    [Fact]
    public void Passes_rejects_a_face_looking_too_far_up_or_down()
    {
        var ok = new FaceRec { H = 100, Score = 0.9f, Blur = 200, Yaw = 0.1, Roll = 5, Pitch = 0.1 };
        Assert.True(CastLogic.Passes(ok));
        Assert.False(CastLogic.Passes(new FaceRec { H = 100, Score = 0.9f, Blur = 200, Yaw = 0.1, Roll = 5, Pitch = 0.277 }));    // Pei Zimo t=5733, looking down
        Assert.False(CastLogic.Passes(new FaceRec { H = 100, Score = 0.9f, Blur = 200, Yaw = 0.1, Roll = 5, Pitch = -0.329 }));   // t=3748, head back
    }

    [Fact]
    public void QualityOf_ranks_a_level_face_with_open_eyes_clearly_above_a_downcast_one()
    {
        FaceRec F(double pitch, double eyes) => new() { H = 160, Score = 0.85f, Blur = 150, Yaw = 0.05, Pitch = pitch, Eyes = eyes };
        double level = CastLogic.QualityOf(F(0, 22), 640), down = CastLogic.QualityOf(F(0.2, 22), 640), shut = CastLogic.QualityOf(F(0, 6), 640), unmeasured = CastLogic.QualityOf(F(0, 0), 640);
        Assert.True(level - down > 0.1);       // looking down costs more than a tenth of the score
        Assert.True(level - shut > 0.05);      // so do dark or closed eyes
        Assert.InRange(unmeasured, shut, level);   // eyes not measured: neutral
        Assert.InRange(level, 0, 1);
    }

    [Fact]
    public void EyeEnergy_reads_texture_around_the_eyes_and_nothing_on_a_flat_patch()
    {
        var flat = Rgb.Blank(100, 100, 128); var k = Kps(77.5f);
        Assert.Equal(0, FaceMath.EyeEnergy(flat, k));
        var rnd = new Random(1); var tex = Rgb.Blank(100, 100, 0); for (int i = 0; i < tex.Data.Length; i++) tex.Data[i] = (byte)rnd.Next(256);
        Assert.True(FaceMath.EyeEnergy(tex, k) > 20);
        Assert.Equal(0, FaceMath.EyeEnergy(tex, [40, 50, 41, 50, 60, 70, 50, 100, 70, 100]));   // eyes 1 px apart: no measure
    }

    [Fact]
    public void SubOnFace_is_true_only_when_the_band_overlaps_the_face_box()
    {
        Assert.False(new FaceRec { X = 100, Y = 100, W = 80, H = 100, Sub = [0, 400, 360, 60] }.SubOnFace);
        Assert.False(new FaceRec { X = 100, Y = 100, W = 80, H = 100 }.SubOnFace);
        Assert.True(new FaceRec { X = 100, Y = 100, W = 80, H = 100, Sub = [0, 180, 360, 60] }.SubOnFace);
    }
}
