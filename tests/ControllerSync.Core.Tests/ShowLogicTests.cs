namespace ControllerSync.Core.Tests;

public class ShowLogicTests
{
    [Theory]
    [InlineData(0x27, false, ShowAction.Next)]
    [InlineData(0x25, false, ShowAction.Previous)]
    [InlineData(0x22, false, ShowAction.Next)]
    [InlineData(0x21, false, ShowAction.Previous)]
    [InlineData(0x20, false, ShowAction.Next)]
    [InlineData(0x08, false, ShowAction.Previous)]
    [InlineData(0x24, false, ShowAction.First)]
    [InlineData(0x23, false, ShowAction.Last)]
    [InlineData(0x1B, false, ShowAction.EndShow)]
    [InlineData(0x74, false, ShowAction.StartFromFirst)]
    [InlineData(0x74, true, ShowAction.StartFromCurrent)]
    [InlineData(0x42, false, ShowAction.BlackScreen)]
    [InlineData(0x57, false, ShowAction.WhiteScreen)]
    public void Presentation_keys_map_to_show_actions(int virtualKey, bool shift, ShowAction expected)
    {
        var action = PresentationKeys.Map(new KeyEvent(virtualKey, 0, KeyPhase.Down, false, false, shift));
        Assert.Equal(expected, action);
    }

    [Fact]
    public void Ctrl_letters_are_not_treated_as_slide_keys()
    {
        var print = new KeyEvent(PresentationKeys.P, 0, KeyPhase.Down, false, true, false);
        Assert.Equal(ShowAction.None, PresentationKeys.Map(print));
        Assert.False(PresentationKeys.IsShowControl(print));
        Assert.True(PresentationKeys.IsShowControl(new KeyEvent(0x31, 0, KeyPhase.Down, false, false, false)));
    }

    [Fact]
    public void While_a_show_is_running_navigation_keys_stay_off_the_wire()
    {
        var right = new KeyEvent(PresentationKeys.Right, 0, KeyPhase.Down, false, false, false);
        Assert.False(InputGate.ShouldSendKey(publishSlides: true, localInShow: true, right));
        Assert.True(InputGate.ShouldSendKey(publishSlides: true, localInShow: false, right));
        Assert.True(InputGate.ShouldSendKey(publishSlides: false, localInShow: true, right));
        Assert.False(InputGate.ShouldInjectKey(followSlides: true, remoteInShow: true, right));
        Assert.False(InputGate.ShouldSendMouse(true, true, MousePhase.Down, true, false));
        Assert.True(InputGate.ShouldSendMouse(true, false, MousePhase.Down, true, false));
        Assert.False(InputGate.ShouldSendMouse(false, false, MousePhase.Move, true, false));
        Assert.True(InputGate.ShouldSendMouse(false, false, MousePhase.Move, true, true));
    }

    [Fact]
    public void Follower_does_not_echo_a_slide_it_just_applied()
    {
        var policy = new SlideFollowPolicy { PublishEnabled = true };
        var remote = new SlideEvent("deck.pptx", 4, 1, 10, true, ShowScreen.Running);
        policy.NoteRemoteApplied(remote);
        Assert.False(policy.ShouldPublish(remote with { DeckName = @"D:\other\deck.pptx" }, heartbeatDue: true));

        var local = remote with { SlideIndex = 5 };
        Assert.True(policy.ShouldPublish(local, heartbeatDue: false));
        Assert.False(policy.ShouldPublish(local, heartbeatDue: false));
        Assert.True(policy.ShouldPublish(local, heartbeatDue: true));
    }

    [Fact]
    public void Backup_with_publish_off_never_sends_slides()
    {
        var policy = new SlideFollowPolicy { PublishEnabled = false };
        var slide = new SlideEvent("deck.pptx", 1, 0, 3, true, ShowScreen.Running);
        Assert.False(policy.ShouldPublish(slide, heartbeatDue: true));
    }

    [Fact]
    public void Cue_text_names_the_operator_action()
    {
        Assert.Equal("NEXT", CueText.Label(ShowAction.Next));
        var slide = SyncMessage.Create("a", "A", "show", SyncKind.Slide, 1);
        slide.Slide = new SlideEvent("deck", 8, 3, 20, true, ShowScreen.Running);
        Assert.Equal("SLIDE 8  ·  3", CueText.From(slide));
        slide.Slide = slide.Slide with { InShow = false, Screen = ShowScreen.Done };
        Assert.Equal("SHOW ENDED", CueText.From(slide));
    }
}
