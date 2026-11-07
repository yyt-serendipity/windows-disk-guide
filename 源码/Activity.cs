using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DiskGuide
{
    internal sealed partial class Shell
    {
        private readonly Dictionary<string,string> activities = new Dictionary<string,string>();
        private string activityResult;
        private bool activityAttention;
        private readonly RotateTransform activityRotation = new RotateTransform();
        private readonly System.Windows.Threading.DispatcherTimer activityTimer = new System.Windows.Threading.DispatcherTimer { Interval=TimeSpan.FromSeconds(8) };
        internal Func<bool> MotionAllowed = ()=>SystemParameters.ClientAreaAnimation;
        private void WireActivity()
        {
            var glyph=Get<System.Windows.Shapes.Path>("ActivityGlyph");
            glyph.RenderTransform=activityRotation;
            glyph.IsVisibleChanged+=delegate{AnimateActivity();};
            Get<Border>("ActivityPanel").IsVisibleChanged+=delegate{
                Get<TextBlock>("StatusText").Visibility=Get<Border>("ActivityPanel").Visibility==Visibility.Visible ? Visibility.Hidden : Visibility.Visible;
            };
            activityTimer.Tick+=delegate{activityTimer.Stop();if(activities.Count==0)Get<Border>("ActivityPanel").Visibility=Visibility.Collapsed;};
            View.StateChanged+=delegate{AnimateActivity();};
            PropertyChangedEventHandler settings=delegate(object sender,PropertyChangedEventArgs e){
                if(e.PropertyName=="ClientAreaAnimation")View.Dispatcher.BeginInvoke(new Action(AnimateActivity));
            };
            SystemParameters.StaticPropertyChanged+=settings;
            View.Closed+=delegate{
                SystemParameters.StaticPropertyChanged-=settings;
                activityTimer.Stop();
                activityRotation.BeginAnimation(RotateTransform.AngleProperty,null);
                Get<Border>("ActivityPanel").BeginAnimation(UIElement.OpacityProperty,null);
                if(startupIcons!=null){startupIcons.Cancel();startupIcons.Dispose();}
                if(softwareIcons!=null){softwareIcons.Cancel();softwareIcons.Dispose();}
            };
            Get<Button>("ActivityDismiss").Click+=delegate{
                if(activities.Count==0)Get<Border>("ActivityPanel").Visibility=Visibility.Collapsed;
            };
        }
        internal void Activity(string key,string text)
        {
            if(lifetime.IsCancellationRequested)return;
            activities[key]=text; RenderActivity();
        }
        internal void EndActivity(string key,string text,bool attention)
        {
            if(!activities.Remove(key) || lifetime.IsCancellationRequested)return;
            activityResult=text;activityAttention=attention;RenderActivity();
        }
        private void RenderActivity()
        {
            var panel=Get<Border>("ActivityPanel");
            bool entering=panel.Visibility!=Visibility.Visible;
            bool running=activities.Count>0;
            activityTimer.Stop();if(!running)activityTimer.Start();
            string text=running ? String.Join(" · ",activities.Values) : activityResult;
            Label("ActivityText",text);Get<TextBlock>("ActivityText").ToolTip=text;
            Label("ActivityState",running ? (activities.Count>1 ? activities.Count+" 项处理中" : "处理中") : (activityAttention ? "请查看结果" : "已完成"));
            Get<Button>("ActivityDismiss").Visibility=running ? Visibility.Collapsed : Visibility.Visible;
            var glyph=Get<System.Windows.Shapes.Path>("ActivityGlyph");
            glyph.Data=Geometry.Parse(running ? "M 12,3 A 9,9 0 1 1 3,12" : activityAttention ? "M 12,3 L 22,21 L 2,21 Z M 12,8 L 12,14 M 12,17 L 12,18" : "M 4,12 L 9,17 L 20,6");
            glyph.Stroke=new SolidColorBrush(activityAttention && !running ? Color.FromRgb(151,99,25) : Color.FromRgb(42,100,199));
            panel.Visibility=Visibility.Visible;
            if(entering && MotionAllowed())
                panel.BeginAnimation(UIElement.OpacityProperty,new DoubleAnimation(0.45,1,TimeSpan.FromMilliseconds(180)){FillBehavior=FillBehavior.Stop});
            AnimateActivity();
        }
        private void AnimateActivity()
        {
            activityRotation.BeginAnimation(RotateTransform.AngleProperty,null);activityRotation.Angle=0;
            bool motion=MotionAllowed() && View.WindowState!=WindowState.Minimized && Get<System.Windows.Shapes.Path>("ActivityGlyph").IsVisible;
            if(!motion)Get<Border>("ActivityPanel").BeginAnimation(UIElement.OpacityProperty,null);
            if(!lifetime.IsCancellationRequested && activities.Count>0 && motion)
                activityRotation.BeginAnimation(RotateTransform.AngleProperty,new DoubleAnimation(0,360,TimeSpan.FromMilliseconds(1000)){RepeatBehavior=RepeatBehavior.Forever});
        }
    }
}
